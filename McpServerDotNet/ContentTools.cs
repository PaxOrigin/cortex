using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace McpServerDotNet;

[McpServerToolType]
public static class ContentTools
{
    // ── TEXT ───────────────────────────────────────────────────────
    // You already know this one. Explicit return for clarity.

    [McpServerTool, Description("Returns a markdown-formatted report")]
    public static TextContentBlock GetMarkdownReport()
        => new()
        {
            Text = """
                   # Sprint 8 Report

                   ## Content Types Covered
                   - **Text** — markdown, plain text, JSON
                   - **Image** — base64 PNG/JPEG
                   - **Audio** — base64 WAV/MP3
                   - **Embedded Resource** — URI-addressed data

                   ## Status
                   All content types _working_.
                   """
        };

    // ── IMAGE ──────────────────────────────────────────────────────
    // A 8x8 red pixel PNG — smallest valid PNG possible.
    // In a real tool this would be a chart, screenshot, diagram, etc.

    [McpServerTool, Description("Returns a small PNG image as an ImageContentBlock")]
    public static ImageContentBlock GetTinyImage()
    {
        // Minimal valid 8x8 solid red PNG (hardcoded bytes — no external libs needed)
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAFklEQVQI12P8" +
            "z8BQDwADhAF/Qc2rNAAAAABJRU5ErkJggg=="
        );
        return ImageContentBlock.FromBytes(png, "image/png");
    }

    // ── EMBEDDED RESOURCE ──────────────────────────────────────────
    // Embeds a resource directly in the tool result.
    // The client receives a URI + content together —
    // useful when you want to return data AND tell the client
    // where that data "lives" in the resource address space.

    [McpServerTool, Description("Returns a config object as an embedded text resource")]
    public static EmbeddedResourceBlock GetEmbeddedConfig()
        => new()
        {
            Resource = new TextResourceContents
            {
                Uri = "config://server/embedded-demo",
                MimeType = "application/json",
                Text = """
                           {
                             "feature_flags": {
                               "new_ui":    true,
                               "dark_mode": false
                             },
                             "max_retries": 3
                           }
                           """
            }
        };

    // ── MIXED ──────────────────────────────────────────────────────
    // One tool, multiple content blocks.
    // The LLM receives all of them in order.
    // This is how you return "here is the description AND the image".

    [McpServerTool, Description("Returns a text block followed by an image block")]
    public static IEnumerable<ContentBlock> GetMixedContent()
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAFklEQVQI12P8" +
            "z8BQDwADhAF/Qc2rNAAAAABJRU5ErkJggg=="
        );

        return
        [
            new TextContentBlock { Text = "Here is the image you requested:" },
            ImageContentBlock.FromBytes(png, "image/png"),
            new TextContentBlock { Text = "Image is 8x8 pixels, solid red, PNG format." }
        ];
    }
}