use std::sync::Arc;

use anyhow::{anyhow, Result};
use lopdf::{Document, Object, Stream};
use vello::peniko::{Blob, ImageData, ImageFormat};

pub struct DecodedImage {
    pub image_data: ImageData,
    pub pdf_width: f64,
    pub pdf_height: f64,
}

pub fn decode_image_xobject(doc: &Document, stream: &Stream) -> Result<DecodedImage> {
    let dict = &stream.dict;

    let width = get_dict_u32(doc, dict, b"Width")
        .or_else(|| get_dict_u32(doc, dict, b"W"))
        .ok_or_else(|| anyhow!("Image missing Width"))?;
    let height = get_dict_u32(doc, dict, b"Height")
        .or_else(|| get_dict_u32(doc, dict, b"H"))
        .ok_or_else(|| anyhow!("Image missing Height"))?;
    let _bpc = get_dict_u32(doc, dict, b"BitsPerComponent").unwrap_or(8);
    let color_space = get_color_space(doc, dict);

    let filter = get_filter_name(dict);

    let rgba_data = match filter.as_deref() {
        Some("DCTDecode") => {
            // Raw JPEG data — decode with image crate
            let raw_data = &stream.content;
            let img = image::load_from_memory_with_format(raw_data, image::ImageFormat::Jpeg)?;
            img.to_rgba8().into_raw()
        }
        _ => {
            // Get decompressed pixel data
            let data = stream
                .decompressed_content()
                .unwrap_or_else(|_| stream.content.clone());
            convert_to_rgba(&data, width, height, &color_space)?
        }
    };

    let image_data = ImageData {
        data: Blob::new(Arc::new(rgba_data.into_boxed_slice())),
        format: ImageFormat::Rgba8,
        alpha_type: vello::peniko::ImageAlphaType::Alpha,
        width,
        height,
    };

    Ok(DecodedImage {
        image_data,
        pdf_width: width as f64,
        pdf_height: height as f64,
    })
}

#[derive(Debug)]
enum ColorSpace {
    DeviceRGB,
    DeviceGray,
    DeviceCMYK,
}

fn get_color_space(doc: &Document, dict: &lopdf::Dictionary) -> ColorSpace {
    let cs_obj = match dict.get(b"ColorSpace").or_else(|_| dict.get(b"CS")) {
        Ok(obj) => obj,
        Err(_) => return ColorSpace::DeviceRGB,
    };

    match cs_obj {
        Object::Name(name) => match name.as_slice() {
            b"DeviceRGB" | b"RGB" => ColorSpace::DeviceRGB,
            b"DeviceGray" | b"G" => ColorSpace::DeviceGray,
            b"DeviceCMYK" | b"CMYK" => ColorSpace::DeviceCMYK,
            _ => ColorSpace::DeviceRGB,
        },
        Object::Reference(id) => {
            if let Ok(Object::Name(name)) = doc.get_object(*id) {
                match name.as_slice() {
                    b"DeviceRGB" => ColorSpace::DeviceRGB,
                    b"DeviceGray" => ColorSpace::DeviceGray,
                    b"DeviceCMYK" => ColorSpace::DeviceCMYK,
                    _ => ColorSpace::DeviceRGB,
                }
            } else {
                ColorSpace::DeviceRGB
            }
        }
        Object::Array(arr) => {
            // Color space can be [/ICCBased ref] or [/Indexed /DeviceRGB ...]
            if let Some(Object::Name(name)) = arr.first() {
                match name.as_slice() {
                    b"DeviceRGB" => ColorSpace::DeviceRGB,
                    b"DeviceGray" => ColorSpace::DeviceGray,
                    b"DeviceCMYK" => ColorSpace::DeviceCMYK,
                    b"ICCBased" => {
                        // Try to determine from the ICC profile's /N (number of components)
                        if let Some(Object::Reference(id)) = arr.get(1) {
                            if let Ok(Object::Stream(ref s)) = doc.get_object(*id) {
                                let n = s
                                    .dict
                                    .get(b"N")
                                    .ok()
                                    .and_then(|o| match o {
                                        Object::Integer(i) => Some(*i),
                                        _ => None,
                                    })
                                    .unwrap_or(3);
                                match n {
                                    1 => return ColorSpace::DeviceGray,
                                    4 => return ColorSpace::DeviceCMYK,
                                    _ => return ColorSpace::DeviceRGB,
                                }
                            }
                        }
                        ColorSpace::DeviceRGB
                    }
                    _ => ColorSpace::DeviceRGB,
                }
            } else {
                ColorSpace::DeviceRGB
            }
        }
        _ => ColorSpace::DeviceRGB,
    }
}

fn get_filter_name(dict: &lopdf::Dictionary) -> Option<String> {
    match dict.get(b"Filter") {
        Ok(Object::Name(name)) => Some(String::from_utf8_lossy(name).to_string()),
        Ok(Object::Array(arr)) => {
            // Last filter in the array is the one applied to the final data
            if let Some(Object::Name(name)) = arr.last() {
                Some(String::from_utf8_lossy(name).to_string())
            } else {
                None
            }
        }
        _ => None,
    }
}

fn convert_to_rgba(
    data: &[u8],
    width: u32,
    height: u32,
    color_space: &ColorSpace,
) -> Result<Vec<u8>> {
    let pixel_count = (width * height) as usize;
    let mut rgba = vec![255u8; pixel_count * 4];

    match color_space {
        ColorSpace::DeviceRGB => {
            let available = data.len() / 3;
            for i in 0..pixel_count.min(available) {
                rgba[i * 4] = data[i * 3];
                rgba[i * 4 + 1] = data[i * 3 + 1];
                rgba[i * 4 + 2] = data[i * 3 + 2];
            }
        }
        ColorSpace::DeviceGray => {
            for i in 0..pixel_count.min(data.len()) {
                rgba[i * 4] = data[i];
                rgba[i * 4 + 1] = data[i];
                rgba[i * 4 + 2] = data[i];
            }
        }
        ColorSpace::DeviceCMYK => {
            let available = data.len() / 4;
            for i in 0..pixel_count.min(available) {
                let c = data[i * 4] as f64 / 255.0;
                let m = data[i * 4 + 1] as f64 / 255.0;
                let y = data[i * 4 + 2] as f64 / 255.0;
                let k = data[i * 4 + 3] as f64 / 255.0;
                rgba[i * 4] = ((1.0 - c) * (1.0 - k) * 255.0) as u8;
                rgba[i * 4 + 1] = ((1.0 - m) * (1.0 - k) * 255.0) as u8;
                rgba[i * 4 + 2] = ((1.0 - y) * (1.0 - k) * 255.0) as u8;
            }
        }
    }
    Ok(rgba)
}

fn get_dict_u32(doc: &Document, dict: &lopdf::Dictionary, key: &[u8]) -> Option<u32> {
    match dict.get(key) {
        Ok(Object::Integer(i)) => Some(*i as u32),
        Ok(Object::Real(f)) => Some(*f as u32),
        Ok(Object::Reference(id)) => match doc.get_object(*id) {
            Ok(Object::Integer(i)) => Some(*i as u32),
            Ok(Object::Real(f)) => Some(*f as u32),
            _ => None,
        },
        _ => None,
    }
}
