# Podcast Pitch Email

## Subject Line Options

Pick one depending on the show's angle:

- **For AI/tech shows (Hard Fork, Vergecast, Changelog):**
  "Vibe coding for the 'missing middle' of accessibility (AI building AI)"

- **For accessibility shows (A11y Rules, Accessible Stall, Blind Abilities):**
  "An open-source PDF viewer built for the gap between sighted and blind"

- **For developer shows (Software Engineering Daily, FLOSS Weekly):**
  "How an AI coding agent built a desktop app with GPU rendering and ONNX inference"

---

## Email Body

Personalise the opening line for each show. Replace [HOST] and [SHOW] as appropriate. The core pitch below works for all targets; adjust emphasis using the notes in brackets.

---

Hi [HOST],

I'm a statistics lecturer at Nelson Mandela University in South Africa, and I'm visually impaired. I wanted to share a project that sits at the intersection of AI-assisted development and accessibility, which I think would resonate with [SHOW]'s audience.

I have poor but usable vision. I can read at normal magnification, but it's a strain — for any sustained reading, high magnification is a practical necessity. The problem is that the accessibility landscape offers two extremes: standard software assumes normal sight, and assistive tools assume full blindness. People with usable but impaired vision fall through the gap. It's too small a market for any software company to build for.

So I built my own solution using Claude Code, Anthropic's AI coding agent: **RailReader2**, an open-source PDF viewer designed for high-magnification reading.

[For AI/tech shows, include this paragraph:]
What makes it particularly interesting is the "AI inception" of it all. To solve the navigation challenges of zoomed-in PDFs, I had Claude Code integrate PP-DocLayoutV3, an AI layout analysis model by PaddlePaddle. It detects document structure (text blocks, headings, figures, footnotes) and predicts reading order, then the viewer guides you through the document line by line. It's an AI coding agent that wrote the code for an accessibility app that itself uses an AI vision model to function.

[For accessibility shows, include this paragraph:]
The viewer uses an AI layout analysis model to detect document structure: text blocks, headings, figures, footnotes, and their reading order. When you zoom in, it switches to "rail mode" and guides you through the document line by line, block by block. It also provides GPU-accelerated colour filters (amber, high contrast, inverted) for additional comfort. It's free, open source, and available for Linux and Windows.

[For developer shows, include this paragraph:]
The technical stack includes .NET 10 with Avalonia 11 for cross-platform UI, PDFium for PDF rasterisation, SkiaSharp 3 for GPU-accelerated rendering with custom SkSL shaders, and PP-DocLayoutV3 running via ONNX Runtime for real-time layout detection. The entire codebase was developed through AI-assisted coding, which I'd be happy to discuss in detail: what worked well, where it struggled, and how it changed my development process.

I'm happy to do a demo, walk through the code, or simply share the story of building it. You can find the project here: https://github.com/sjvrensburg/railreader2

Thanks for your time, and for the excellent work on [SHOW].

Best regards,
Stefan Janse van Rensburg
Senior Lecturer in Statistics, Nelson Mandela University
South Africa

---

## Follow-Up Email (7-10 days later, if no response)

Subject: Re: [original subject]

Hi [HOST],

Just a quick follow-up on my earlier note about RailReader2. I appreciate you're likely busy, so I'll keep this brief: if the story interests you, I'm available at your convenience. If the timing isn't right, no worries at all.

Best,
Stefan
