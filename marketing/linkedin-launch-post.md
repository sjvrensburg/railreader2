# LinkedIn Launch Post — RailReader2

## Title

"AI Wrote the Code. AI Reads the Page. I Can Finally Read Comfortably."

## Body

I'm visually impaired, and I've spent years fighting with PDF viewers.

The problem is specific: I have poor but usable vision. I can read at normal magnification, but it's a strain — for any sustained reading, high magnification is a practical necessity. And high magnification turns PDF reading into an exercise in frustration. You lose your place constantly. Scrolling is inefficient. Context disappears. Most viewers simply weren't designed for sustained use at 3x zoom and above.

The accessibility landscape offers two extremes. Standard software assumes normal sight. Assistive tools assume full blindness with screen readers and audio output. The "missing middle", people with usable but impaired vision, falls through the gap. It's too small a market for any software company to address. I understand the economics. But understanding it doesn't make the problem go away.

So I built RailReader2: an open-source PDF viewer designed specifically for high-magnification reading.

It works by using an AI layout analysis model (PP-DocLayoutV3 by PaddlePaddle) to detect text blocks, headings, figures, footnotes, and their reading order on each page. When you zoom in past a threshold, the viewer switches to "rail mode": navigation locks onto detected blocks and advances line by line, like a typewriter carriage return. Non-active regions dim so you can focus on what you're reading. Colour filters (amber, high contrast, inverted) are applied via GPU shaders for additional comfort.

### What interests me about this project

There is an "AI inception" to the whole thing. RailReader2 was built almost entirely with Claude Code, Anthropic's AI coding agent. So it is an AI agent that wrote the code for an accessibility app that itself leverages an AI vision model to function.

I'm a statistics lecturer, not a software developer by trade. A year ago, building a cross-platform desktop application with GPU-accelerated rendering and ONNX inference would have been well beyond my reach. AI-assisted development changed that calculation entirely.

This is the same pattern I described in my earlier post about pdf-to-audio: AI tools are creating unprecedented opportunities for individuals to solve niche accessibility problems that larger organisations overlook. The tools have matured to the point where one person with a specific need can build real, functional software to address it.

RailReader2 is free, open source, and available for Linux and Windows.

GitHub: https://github.com/sjvrensburg/railreader2
Website: https://sjvrensburg.github.io/railreader2/

### Technical Stack
- .NET 10 / Avalonia 11 (cross-platform UI)
- PDFtoImage / PDFium (PDF rasterisation)
- SkiaSharp 3 (GPU-accelerated rendering)
- PP-DocLayoutV3 (ONNX layout detection by PaddlePaddle)
- Built with Claude Code (Anthropic)

If you know someone with low vision who struggles with PDFs, I'd appreciate you sharing this with them.

#Accessibility #AssistiveTech #OpenSource #LowVision #AI #ClaudeCode #VibeCoding
