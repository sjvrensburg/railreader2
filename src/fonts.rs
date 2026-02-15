use std::collections::HashMap;
use std::sync::Arc;

use anyhow::{anyhow, Result};
use lopdf::{Document, Object, ObjectId};
use vello::peniko::{Blob, FontData};

const FALLBACK_FONT_BYTES: &[u8] = include_bytes!("../assets/DejaVuSans.ttf");

pub fn fallback_font() -> FontData {
    FontData::new(Blob::new(Arc::new(FALLBACK_FONT_BYTES.to_vec())), 0)
}

#[derive(Clone, Debug)]
pub struct PdfFont {
    pub font_data: FontData,
    pub units_per_em: u16,
    pub encoding: PdfEncoding,
    pub widths: Vec<f64>,
    pub first_char: u32,
}

#[derive(Clone, Debug)]
pub enum PdfEncoding {
    WinAnsi,
    MacRoman,
    Identity,
    ToUnicode(HashMap<u16, char>),
    Custom(HashMap<u8, char>),
}

/// Extract all fonts referenced by a page's /Resources/Font dictionary.
pub fn extract_page_fonts(doc: &Document, page_id: ObjectId) -> HashMap<Vec<u8>, Arc<PdfFont>> {
    let mut fonts = HashMap::new();

    let font_dict = match get_page_font_dict(doc, page_id) {
        Some(d) => d,
        None => return fonts,
    };

    for (name, obj) in &font_dict {
        let font_obj_id = match obj {
            Object::Reference(id) => *id,
            _ => continue,
        };
        let font_dict = match doc.get_dictionary(font_obj_id) {
            Ok(d) => d,
            Err(_) => continue,
        };
        match extract_font(doc, font_dict) {
            Ok(font) => {
                fonts.insert(name.clone(), Arc::new(font));
            }
            Err(e) => {
                log::warn!(
                    "Failed to extract font '{}': {}",
                    String::from_utf8_lossy(name),
                    e
                );
            }
        }
    }

    fonts
}

fn get_page_font_dict(doc: &Document, page_id: ObjectId) -> Option<lopdf::Dictionary> {
    let page = doc.get_dictionary(page_id).ok()?;

    // Try direct Resources dict
    let resources = match page.get(b"Resources") {
        Ok(Object::Dictionary(d)) => d.clone(),
        Ok(Object::Reference(id)) => doc.get_dictionary(*id).ok()?.clone(),
        _ => return None,
    };

    match resources.get(b"Font") {
        Ok(Object::Dictionary(d)) => Some(d.clone()),
        Ok(Object::Reference(id)) => doc.get_dictionary(*id).ok().cloned(),
        _ => None,
    }
}

fn extract_font(doc: &Document, font_dict: &lopdf::Dictionary) -> Result<PdfFont> {
    let subtype: &[u8] = font_dict
        .get(b"Subtype")
        .ok()
        .and_then(|o| o.as_name().ok())
        .unwrap_or(b"Unknown");

    let encoding = build_encoding(doc, font_dict);

    // For Type0 (composite) fonts, descend to the CIDFont
    let (descriptor_dict, widths, first_char) = if subtype == b"Type0" {
        extract_type0_info(doc, font_dict)?
    } else {
        extract_simple_font_info(doc, font_dict)?
    };

    // Extract embedded font program
    let font_data = if let Some(desc) = &descriptor_dict {
        extract_font_program(doc, desc).unwrap_or_else(|| {
            log::debug!("No embedded font program, using fallback");
            fallback_font()
        })
    } else {
        log::debug!("No font descriptor, using fallback");
        fallback_font()
    };

    // Get units_per_em from the font data
    let units_per_em = skrifa::FontRef::new(font_data.data.as_ref())
        .or_else(|_| skrifa::FontRef::from_index(font_data.data.as_ref(), font_data.index))
        .ok()
        .and_then(|f| {
            use skrifa::raw::TableProvider;
            f.head().ok().map(|h| h.units_per_em())
        })
        .unwrap_or(1000);

    Ok(PdfFont {
        font_data,
        units_per_em,
        encoding,
        widths,
        first_char,
    })
}

fn extract_simple_font_info(
    doc: &Document,
    font_dict: &lopdf::Dictionary,
) -> Result<(Option<lopdf::Dictionary>, Vec<f64>, u32)> {
    let first_char = font_dict
        .get(b"FirstChar")
        .ok()
        .and_then(as_u32)
        .unwrap_or(0);

    let widths = extract_widths(doc, font_dict);

    let descriptor = get_font_descriptor(doc, font_dict);

    Ok((descriptor, widths, first_char))
}

fn extract_type0_info(
    doc: &Document,
    font_dict: &lopdf::Dictionary,
) -> Result<(Option<lopdf::Dictionary>, Vec<f64>, u32)> {
    // Get DescendantFonts array
    let descendants = match font_dict.get(b"DescendantFonts") {
        Ok(Object::Array(arr)) => arr,
        Ok(Object::Reference(id)) => match doc.get_object(*id) {
            Ok(Object::Array(arr)) => arr,
            _ => return Ok((None, vec![], 0)),
        },
        _ => return Ok((None, vec![], 0)),
    };

    let cid_font_ref = match descendants.first() {
        Some(Object::Reference(id)) => *id,
        _ => return Ok((None, vec![], 0)),
    };

    let cid_dict = doc
        .get_dictionary(cid_font_ref)
        .map_err(|e| anyhow!("Failed to get CIDFont dict: {}", e))?;

    let descriptor = get_font_descriptor(doc, cid_dict);

    // CID fonts: extract DW (default width) and W (width array)
    let default_width = cid_dict
        .get(b"DW")
        .ok()
        .and_then(as_f64_obj)
        .unwrap_or(1000.0);

    let widths = extract_cid_widths(doc, cid_dict, default_width);
    Ok((descriptor, widths, 0))
}

fn extract_cid_widths(
    doc: &Document,
    cid_dict: &lopdf::Dictionary,
    default_width: f64,
) -> Vec<f64> {
    // CID Width format: W [ cid1 [w1 w2 ...] cid2 cid3 w ... ]
    // For simplicity, build a sparse map then convert to a vec
    let w_array = match cid_dict.get(b"W") {
        Ok(Object::Array(arr)) => arr,
        Ok(Object::Reference(id)) => match doc.get_object(*id) {
            Ok(Object::Array(arr)) => arr,
            _ => return vec![default_width; 256],
        },
        _ => return vec![default_width; 256],
    };

    let mut width_map: HashMap<u32, f64> = HashMap::new();
    let mut max_cid: u32 = 255;
    let mut i = 0;

    while i < w_array.len() {
        let cid_start = match as_u32(&w_array[i]) {
            Some(v) => v,
            None => {
                i += 1;
                continue;
            }
        };
        i += 1;
        if i >= w_array.len() {
            break;
        }

        match &w_array[i] {
            Object::Array(widths) => {
                // cid [w1 w2 w3 ...]
                for (j, w) in widths.iter().enumerate() {
                    let cid = cid_start + j as u32;
                    if let Some(wv) = as_f64_obj(w) {
                        width_map.insert(cid, wv);
                        max_cid = max_cid.max(cid);
                    }
                }
                i += 1;
            }
            _ => {
                // cid_start cid_end w
                let cid_end = match as_u32(&w_array[i]) {
                    Some(v) => v,
                    None => {
                        i += 1;
                        continue;
                    }
                };
                i += 1;
                let w = if i < w_array.len() {
                    as_f64_obj(&w_array[i]).unwrap_or(default_width)
                } else {
                    default_width
                };
                i += 1;
                for cid in cid_start..=cid_end {
                    width_map.insert(cid, w);
                    max_cid = max_cid.max(cid);
                }
            }
        }
    }

    let mut widths = vec![default_width; (max_cid + 1) as usize];
    for (cid, w) in width_map {
        if (cid as usize) < widths.len() {
            widths[cid as usize] = w;
        }
    }
    widths
}

fn get_font_descriptor(doc: &Document, font_dict: &lopdf::Dictionary) -> Option<lopdf::Dictionary> {
    match font_dict.get(b"FontDescriptor") {
        Ok(Object::Dictionary(d)) => Some(d.clone()),
        Ok(Object::Reference(id)) => doc.get_dictionary(*id).ok().cloned(),
        _ => None,
    }
}

fn extract_font_program(doc: &Document, descriptor: &lopdf::Dictionary) -> Option<FontData> {
    // Try FontFile2 (TrueType), FontFile3 (CFF/OpenType), FontFile (Type1)
    for key in &[b"FontFile2" as &[u8], b"FontFile3", b"FontFile"] {
        if let Ok(obj) = descriptor.get(key) {
            let stream_id = match obj {
                Object::Reference(id) => *id,
                _ => continue,
            };
            if let Ok(Object::Stream(ref s)) = doc.get_object(stream_id) {
                if let Ok(data) = s.decompressed_content() {
                    if !data.is_empty() {
                        return Some(FontData::new(Blob::new(Arc::new(data)), 0));
                    }
                }
            }
        }
    }
    None
}

fn extract_widths(doc: &Document, font_dict: &lopdf::Dictionary) -> Vec<f64> {
    let widths_obj = match font_dict.get(b"Widths") {
        Ok(Object::Array(arr)) => arr.clone(),
        Ok(Object::Reference(id)) => match doc.get_object(*id) {
            Ok(Object::Array(arr)) => arr.clone(),
            _ => return vec![],
        },
        _ => return vec![],
    };

    widths_obj
        .iter()
        .map(|o| as_f64_obj(o).unwrap_or(0.0))
        .collect()
}

fn build_encoding(doc: &Document, font_dict: &lopdf::Dictionary) -> PdfEncoding {
    // Check for ToUnicode CMap first (highest priority)
    if let Some(enc) = try_parse_tounicode(doc, font_dict) {
        return enc;
    }

    // Check /Encoding
    match font_dict.get(b"Encoding") {
        Ok(Object::Name(name)) => match name.as_slice() {
            b"WinAnsiEncoding" => PdfEncoding::WinAnsi,
            b"MacRomanEncoding" => PdfEncoding::MacRoman,
            b"Identity-H" | b"Identity-V" => PdfEncoding::Identity,
            _ => PdfEncoding::WinAnsi, // default fallback
        },
        Ok(Object::Dictionary(enc_dict)) => {
            // Encoding dict with possible /Differences
            let mut base = win_ansi_table();
            if let Ok(Object::Name(base_name)) = enc_dict.get(b"BaseEncoding") {
                if base_name == b"MacRomanEncoding" {
                    base = mac_roman_table();
                }
            }
            if let Ok(Object::Array(diffs)) = enc_dict.get(b"Differences") {
                apply_differences(&mut base, diffs);
            }
            PdfEncoding::Custom(base)
        }
        Ok(Object::Reference(id)) => {
            if let Ok(Object::Dictionary(enc_dict)) = doc.get_object(*id) {
                let mut base = win_ansi_table();
                if let Ok(Object::Name(base_name)) = enc_dict.get(b"BaseEncoding") {
                    if base_name == b"MacRomanEncoding" {
                        base = mac_roman_table();
                    }
                }
                if let Ok(Object::Array(diffs)) = enc_dict.get(b"Differences") {
                    apply_differences(&mut base, diffs);
                }
                PdfEncoding::Custom(base)
            } else if let Ok(Object::Name(name)) = doc.get_object(*id) {
                match name.as_slice() {
                    b"WinAnsiEncoding" => PdfEncoding::WinAnsi,
                    b"MacRomanEncoding" => PdfEncoding::MacRoman,
                    b"Identity-H" | b"Identity-V" => PdfEncoding::Identity,
                    _ => PdfEncoding::WinAnsi,
                }
            } else {
                PdfEncoding::Identity
            }
        }
        _ => {
            // No encoding specified; for Type0 fonts this usually means Identity
            let subtype: &[u8] = font_dict
                .get(b"Subtype")
                .ok()
                .and_then(|o| o.as_name().ok())
                .unwrap_or(b"");
            if subtype == b"Type0" {
                PdfEncoding::Identity
            } else {
                PdfEncoding::WinAnsi
            }
        }
    }
}

fn try_parse_tounicode(doc: &Document, font_dict: &lopdf::Dictionary) -> Option<PdfEncoding> {
    let tu_obj = font_dict.get(b"ToUnicode").ok()?;
    let stream_id = match tu_obj {
        Object::Reference(id) => *id,
        _ => return None,
    };
    let stream = match doc.get_object(stream_id) {
        Ok(Object::Stream(ref s)) => s,
        _ => return None,
    };
    let data = stream.decompressed_content().ok()?;
    let map = parse_to_unicode_cmap(&data);
    if map.is_empty() {
        None
    } else {
        Some(PdfEncoding::ToUnicode(map))
    }
}

fn parse_to_unicode_cmap(data: &[u8]) -> HashMap<u16, char> {
    let text = String::from_utf8_lossy(data);
    let mut map = HashMap::new();

    // Parse beginbfchar ... endbfchar
    let mut remaining = text.as_ref();
    while let Some(start) = remaining.find("beginbfchar") {
        let chunk_start = start + "beginbfchar".len();
        let chunk_end = remaining[chunk_start..]
            .find("endbfchar")
            .map(|i| chunk_start + i)
            .unwrap_or(remaining.len());
        let chunk = &remaining[chunk_start..chunk_end];

        // Parse pairs: <srcCode> <dstUnicode>
        let hex_values: Vec<u16> = extract_hex_values(chunk);
        for pair in hex_values.chunks(2) {
            if pair.len() == 2 {
                if let Some(ch) = char::from_u32(pair[1] as u32) {
                    map.insert(pair[0], ch);
                }
            }
        }

        remaining = &remaining[chunk_end..];
    }

    // Parse beginbfrange ... endbfrange
    remaining = text.as_ref();
    while let Some(start) = remaining.find("beginbfrange") {
        let chunk_start = start + "beginbfrange".len();
        let chunk_end = remaining[chunk_start..]
            .find("endbfrange")
            .map(|i| chunk_start + i)
            .unwrap_or(remaining.len());
        let chunk = &remaining[chunk_start..chunk_end];

        // Parse triples: <start> <end> <dstStart>
        let hex_values: Vec<u16> = extract_hex_values(chunk);
        for triple in hex_values.chunks(3) {
            if triple.len() == 3 {
                let (range_start, range_end, dst_start) = (triple[0], triple[1], triple[2]);
                for code in range_start..=range_end {
                    let unicode = dst_start + (code - range_start);
                    if let Some(ch) = char::from_u32(unicode as u32) {
                        map.insert(code, ch);
                    }
                }
            }
        }

        remaining = &remaining[chunk_end..];
    }

    map
}

fn extract_hex_values(text: &str) -> Vec<u16> {
    let mut values = Vec::new();
    let mut i = 0;
    let bytes = text.as_bytes();

    while i < bytes.len() {
        if bytes[i] == b'<' {
            let start = i + 1;
            if let Some(end_offset) = text[start..].find('>') {
                let hex_str = &text[start..start + end_offset];
                let hex_clean: String = hex_str.chars().filter(|c| c.is_ascii_hexdigit()).collect();
                if let Ok(val) = u16::from_str_radix(&hex_clean, 16) {
                    values.push(val);
                }
                i = start + end_offset + 1;
            } else {
                i += 1;
            }
        } else {
            i += 1;
        }
    }

    values
}

/// Decode a PDF string using the given encoding.
/// Returns Vec of (Unicode char, original byte code for width lookup).
pub fn decode_string(bytes: &[u8], encoding: &PdfEncoding) -> Vec<(char, u16)> {
    match encoding {
        PdfEncoding::WinAnsi => bytes
            .iter()
            .map(|&b| {
                let ch = win_ansi_char(b);
                (ch, b as u16)
            })
            .collect(),
        PdfEncoding::MacRoman => bytes
            .iter()
            .map(|&b| {
                let ch = mac_roman_char(b);
                (ch, b as u16)
            })
            .collect(),
        PdfEncoding::Identity => {
            // Two bytes per character code
            let mut result = Vec::new();
            let mut i = 0;
            while i + 1 < bytes.len() {
                let code = ((bytes[i] as u16) << 8) | (bytes[i + 1] as u16);
                let ch = char::from_u32(code as u32).unwrap_or('\u{FFFD}');
                result.push((ch, code));
                i += 2;
            }
            if i < bytes.len() {
                // Odd byte at end
                let code = bytes[i] as u16;
                let ch = char::from_u32(code as u32).unwrap_or('\u{FFFD}');
                result.push((ch, code));
            }
            result
        }
        PdfEncoding::ToUnicode(map) => {
            // Try two-byte codes first, fall back to single-byte
            if bytes.len() >= 2 {
                let test_code = ((bytes[0] as u16) << 8) | (bytes[1] as u16);
                if map.contains_key(&test_code) {
                    // Likely two-byte encoding
                    let mut result = Vec::new();
                    let mut i = 0;
                    while i + 1 < bytes.len() {
                        let code = ((bytes[i] as u16) << 8) | (bytes[i + 1] as u16);
                        let ch = map.get(&code).copied().unwrap_or('\u{FFFD}');
                        result.push((ch, code));
                        i += 2;
                    }
                    return result;
                }
            }
            // Single-byte
            bytes
                .iter()
                .map(|&b| {
                    let code = b as u16;
                    let ch = map.get(&code).copied().unwrap_or(b as char);
                    (ch, code)
                })
                .collect()
        }
        PdfEncoding::Custom(table) => bytes
            .iter()
            .map(|&b| {
                let ch = table.get(&b).copied().unwrap_or(b as char);
                (ch, b as u16)
            })
            .collect(),
    }
}

fn apply_differences(base: &mut HashMap<u8, char>, diffs: &[Object]) {
    let mut code: u8 = 0;
    for obj in diffs {
        match obj {
            Object::Integer(i) => {
                code = *i as u8;
            }
            Object::Name(name) => {
                if let Some(ch) = glyph_name_to_unicode(name) {
                    base.insert(code, ch);
                }
                code = code.wrapping_add(1);
            }
            _ => {}
        }
    }
}

fn glyph_name_to_unicode(name: &[u8]) -> Option<char> {
    let name_str = std::str::from_utf8(name).ok()?;

    // Handle "uniXXXX" names
    if let Some(hex) = name_str.strip_prefix("uni") {
        if let Ok(val) = u32::from_str_radix(hex, 16) {
            return char::from_u32(val);
        }
    }

    // Common Adobe Glyph List mappings (abbreviated)
    match name_str {
        "space" => Some(' '),
        "exclam" => Some('!'),
        "quotedbl" => Some('"'),
        "numbersign" => Some('#'),
        "dollar" => Some('$'),
        "percent" => Some('%'),
        "ampersand" => Some('&'),
        "quotesingle" => Some('\''),
        "parenleft" => Some('('),
        "parenright" => Some(')'),
        "asterisk" => Some('*'),
        "plus" => Some('+'),
        "comma" => Some(','),
        "hyphen" | "minus" => Some('-'),
        "period" => Some('.'),
        "slash" => Some('/'),
        "zero" => Some('0'),
        "one" => Some('1'),
        "two" => Some('2'),
        "three" => Some('3'),
        "four" => Some('4'),
        "five" => Some('5'),
        "six" => Some('6'),
        "seven" => Some('7'),
        "eight" => Some('8'),
        "nine" => Some('9'),
        "colon" => Some(':'),
        "semicolon" => Some(';'),
        "less" => Some('<'),
        "equal" => Some('='),
        "greater" => Some('>'),
        "question" => Some('?'),
        "at" => Some('@'),
        "bracketleft" => Some('['),
        "backslash" => Some('\\'),
        "bracketright" => Some(']'),
        "asciicircum" => Some('^'),
        "underscore" => Some('_'),
        "grave" => Some('`'),
        "braceleft" => Some('{'),
        "bar" => Some('|'),
        "braceright" => Some('}'),
        "asciitilde" => Some('~'),
        "bullet" => Some('\u{2022}'),
        "endash" => Some('\u{2013}'),
        "emdash" => Some('\u{2014}'),
        "quotedblleft" => Some('\u{201C}'),
        "quotedblright" => Some('\u{201D}'),
        "quoteleft" => Some('\u{2018}'),
        "quoteright" => Some('\u{2019}'),
        "fi" => Some('\u{FB01}'),
        "fl" => Some('\u{FB02}'),
        "ellipsis" => Some('\u{2026}'),
        "dagger" => Some('\u{2020}'),
        "daggerdbl" => Some('\u{2021}'),
        "trademark" => Some('\u{2122}'),
        "copyright" => Some('\u{00A9}'),
        "registered" => Some('\u{00AE}'),
        "degree" => Some('\u{00B0}'),
        _ => {
            // Try single ASCII letter names (A-Z, a-z)
            if name_str.len() == 1 {
                Some(name_str.chars().next().unwrap())
            } else {
                log::trace!("Unknown glyph name: {}", name_str);
                None
            }
        }
    }
}

// --- Encoding tables ---

fn win_ansi_table() -> HashMap<u8, char> {
    let mut m = HashMap::new();
    // ASCII range 32-126 maps directly
    for b in 32u8..=126 {
        m.insert(b, b as char);
    }
    // High bytes (128-255) - WinAnsi specific
    m.insert(128, '\u{20AC}'); // Euro sign
    m.insert(130, '\u{201A}');
    m.insert(131, '\u{0192}');
    m.insert(132, '\u{201E}');
    m.insert(133, '\u{2026}');
    m.insert(134, '\u{2020}');
    m.insert(135, '\u{2021}');
    m.insert(136, '\u{02C6}');
    m.insert(137, '\u{2030}');
    m.insert(138, '\u{0160}');
    m.insert(139, '\u{2039}');
    m.insert(140, '\u{0152}');
    m.insert(142, '\u{017D}');
    m.insert(145, '\u{2018}');
    m.insert(146, '\u{2019}');
    m.insert(147, '\u{201C}');
    m.insert(148, '\u{201D}');
    m.insert(149, '\u{2022}');
    m.insert(150, '\u{2013}');
    m.insert(151, '\u{2014}');
    m.insert(152, '\u{02DC}');
    m.insert(153, '\u{2122}');
    m.insert(154, '\u{0161}');
    m.insert(155, '\u{203A}');
    m.insert(156, '\u{0153}');
    m.insert(158, '\u{017E}');
    m.insert(159, '\u{0178}');
    // Latin-1 supplement 160-255 maps to Unicode directly
    for b in 160u8..=255 {
        m.insert(b, b as char);
    }
    m
}

fn mac_roman_table() -> HashMap<u8, char> {
    let mut m = HashMap::new();
    // ASCII range maps directly
    for b in 32u8..=126 {
        m.insert(b, b as char);
    }
    // Mac Roman high bytes
    let mac_high: [(u8, char); 26] = [
        (128, '\u{00C4}'),
        (129, '\u{00C5}'),
        (130, '\u{00C7}'),
        (131, '\u{00C9}'),
        (132, '\u{00D1}'),
        (133, '\u{00D6}'),
        (134, '\u{00DC}'),
        (135, '\u{00E1}'),
        (136, '\u{00E0}'),
        (137, '\u{00E2}'),
        (138, '\u{00E4}'),
        (139, '\u{00E3}'),
        (140, '\u{00E5}'),
        (141, '\u{00E7}'),
        (142, '\u{00E9}'),
        (143, '\u{00E8}'),
        (144, '\u{00EA}'),
        (145, '\u{00EB}'),
        (146, '\u{00ED}'),
        (147, '\u{00EC}'),
        (148, '\u{00EE}'),
        (149, '\u{00EF}'),
        (150, '\u{00F1}'),
        (151, '\u{00F3}'),
        (152, '\u{00F2}'),
        (153, '\u{00F4}'),
    ];
    for (b, c) in &mac_high {
        m.insert(*b, *c);
    }
    m
}

fn win_ansi_char(b: u8) -> char {
    // Fast path for common ASCII
    if (32..=126).contains(&b) {
        return b as char;
    }
    if (160..=255).contains(&b) {
        return b as char;
    }
    match b {
        128 => '\u{20AC}',
        145 => '\u{2018}',
        146 => '\u{2019}',
        147 => '\u{201C}',
        148 => '\u{201D}',
        149 => '\u{2022}',
        150 => '\u{2013}',
        151 => '\u{2014}',
        _ => b as char,
    }
}

fn mac_roman_char(b: u8) -> char {
    if (32..=126).contains(&b) {
        return b as char;
    }
    // Simplified - just return the byte as char for unmapped values
    b as char
}

fn as_u32(obj: &Object) -> Option<u32> {
    match obj {
        Object::Integer(i) => Some(*i as u32),
        Object::Real(f) => Some(*f as u32),
        _ => None,
    }
}

fn as_f64_obj(obj: &Object) -> Option<f64> {
    match obj {
        Object::Integer(i) => Some(*i as f64),
        Object::Real(f) => Some(*f as f64),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_fallback_font_loads() {
        use skrifa::MetadataProvider;
        let font = fallback_font();
        let fref = skrifa::FontRef::new(font.data.as_ref()).unwrap();
        let charmap = fref.charmap();
        assert!(charmap.map('A').is_some());
    }

    #[test]
    fn test_winansi_encoding() {
        let table = win_ansi_table();
        assert_eq!(table[&65], 'A');
        assert_eq!(table[&32], ' ');
        assert_eq!(table[&128], '\u{20AC}');
    }

    #[test]
    fn test_decode_string_winansi() {
        let decoded = decode_string(b"Hello", &PdfEncoding::WinAnsi);
        let text: String = decoded.iter().map(|(c, _)| c).collect();
        assert_eq!(text, "Hello");
    }

    #[test]
    fn test_tounicode_cmap_parse() {
        let cmap = b"beginbfchar\n<0041> <0042>\nendbfchar\n";
        let map = parse_to_unicode_cmap(cmap);
        assert_eq!(map.get(&0x0041), Some(&'B'));
    }
}
