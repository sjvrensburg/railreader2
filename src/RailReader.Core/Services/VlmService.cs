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

    public record VlmResult(string? Text, string? Error);

    private static string GetPrompt(BlockAction action) => action switch
    {
        BlockAction.Markdown =>
            "Convert this table to Markdown format. Return only the Markdown table, no explanation.",
        BlockAction.Description =>
            "Describe this figure briefly in one sentence.",
        _ =>
            "Convert this to LaTeX. Return only the LaTeX code, no explanation, no surrounding delimiters.",
    };

    public static async Task<VlmResult> DescribeBlockAsync(
        byte[] pngBytes, BlockAction action, AppConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.VlmEndpoint))
            return new VlmResult(null, "VLM not configured \u2014 check Settings");

        if (string.IsNullOrWhiteSpace(config.VlmModel))
            return new VlmResult(null, "VLM model not configured \u2014 check Settings");

        try
        {
            var options = new OpenAIClientOptions
            {
                Endpoint = new Uri(config.VlmEndpoint),
            };

            // Use a dummy key if none provided (local endpoints like Ollama don't require one)
            var credential = new ApiKeyCredential(
                string.IsNullOrWhiteSpace(config.VlmApiKey) ? "not-required" : config.VlmApiKey);

            var client = new ChatClient(config.VlmModel, credential, options);

            var imageContent = ChatMessageContentPart.CreateImagePart(
                BinaryData.FromBytes(pngBytes), "image/png");
            var textContent = ChatMessageContentPart.CreateTextPart(GetPrompt(action));

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
            var options = new OpenAIClientOptions
            {
                Endpoint = new Uri(config.VlmEndpoint),
            };

            var credential = new ApiKeyCredential(
                string.IsNullOrWhiteSpace(config.VlmApiKey) ? "not-required" : config.VlmApiKey);

            var client = new ChatClient(config.VlmModel, credential, options);

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
}
