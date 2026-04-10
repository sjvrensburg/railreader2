using System.ClientModel;
using OpenAI;
using OpenAI.Chat;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Sends block crops to an OpenAI-compatible vision API and returns
/// LaTeX, Markdown, or a description depending on the block type.
/// </summary>
public static class VlmService
{
    public enum BlockAction { LaTeX, Markdown, Description }

    /// <summary>
    /// Prompt phrasing style. Instruction-tuned VLMs (Qwen, GPT-4, etc.) follow
    /// explicit "convert to X" directives. OCR-specialised models (LightOnOCR)
    /// respond better to short "transcribe" phrasing and tend to emit HTML for
    /// tables regardless of prompt.
    /// </summary>
    public enum PromptStyle { Instruction, Ocr }

    public record VlmResult(string? Text, string? Error);

    private static string GetPrompt(BlockAction action, PromptStyle style) => (action, style) switch
    {
        (BlockAction.Markdown, PromptStyle.Ocr) =>
            "Transcribe this table.",
        (BlockAction.Description, PromptStyle.Ocr) =>
            "Transcribe the contents of this figure.",
        (BlockAction.LaTeX, PromptStyle.Ocr) =>
            "Transcribe this equation as LaTeX.",
        (BlockAction.Markdown, _) =>
            "Convert this table to Markdown format. Return only the Markdown table, no explanation.",
        (BlockAction.Description, _) =>
            "Describe this figure briefly in one sentence.",
        _ =>
            "Convert this to LaTeX. Return only the LaTeX code, no explanation, no surrounding delimiters.",
    };

    public static async Task<VlmResult> DescribeBlockAsync(
        byte[] pngBytes, BlockAction action, AppConfig config,
        PromptStyle style = PromptStyle.Instruction, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.VlmEndpoint))
            return new VlmResult(null, "VLM not configured \u2014 check Settings");

        if (string.IsNullOrWhiteSpace(config.VlmModel))
            return new VlmResult(null, "VLM model not configured \u2014 check Settings");

        try
        {
            var client = CreateClient(config);

            var imageContent = ChatMessageContentPart.CreateImagePart(
                BinaryData.FromBytes(pngBytes), "image/png");
            var textContent = ChatMessageContentPart.CreateTextPart(GetPrompt(action, style));

            var messages = new List<ChatMessage>
            {
                new UserChatMessage(imageContent, textContent),
            };

            var chatOptions = new ChatCompletionOptions { MaxOutputTokenCount = 1024 };

            var completion = await client.CompleteChatAsync(messages, chatOptions, ct);
            var text = completion.Value.Content[0].Text?.Trim();

            if (string.IsNullOrWhiteSpace(text))
                return new VlmResult(null, "VLM returned empty response");

            return new VlmResult(text, null);
        }
        catch (OperationCanceledException)
        {
            return new VlmResult(null, "Request cancelled");
        }
        catch (ClientResultException ex) when (ex.Status == 401)
        {
            return new VlmResult(null, "Invalid API key");
        }
        catch (ClientResultException ex)
        {
            return new VlmResult(null, $"API error ({ex.Status}): {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return new VlmResult(null, $"Cannot reach VLM endpoint: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new VlmResult(null, $"VLM error: {ex.Message}");
        }
    }

    /// <summary>
    /// Tests the VLM connection by sending a simple text-only request.
    /// Returns null on success, or an error message on failure.
    /// </summary>
    public static async Task<string?> TestConnectionAsync(AppConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.VlmEndpoint))
            return "Enter an endpoint URL first";

        if (string.IsNullOrWhiteSpace(config.VlmModel))
            return "Enter a model name first";

        try
        {
            var client = CreateClient(config);

            var messages = new List<ChatMessage>
            {
                new UserChatMessage("Reply with OK"),
            };

            var chatOptions = new ChatCompletionOptions { MaxOutputTokenCount = 8 };
            await client.CompleteChatAsync(messages, chatOptions, ct);
            return null;
        }
        catch (ClientResultException ex) when (ex.Status == 401)
        {
            return "Invalid API key";
        }
        catch (ClientResultException ex)
        {
            return $"API error ({ex.Status}): {ex.Message}";
        }
        catch (HttpRequestException ex)
        {
            return $"Cannot reach endpoint: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            return "Connection timed out";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static ChatClient CreateClient(AppConfig config)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(config.VlmEndpoint!),
        };
        // Local endpoints like Ollama don't require an API key
        var credential = new ApiKeyCredential(
            string.IsNullOrWhiteSpace(config.VlmApiKey) ? "not-required" : config.VlmApiKey);
        return new ChatClient(config.VlmModel!, credential, options);
    }
}
