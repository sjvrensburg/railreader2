using System.ClientModel;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Sends block crops to an OpenAI-compatible vision API and returns
/// LaTeX, Markdown, or a description depending on the block type.
/// </summary>
/// <summary>Minimal config needed to call a VLM endpoint.</summary>
public record VlmEndpointConfig(string? Endpoint, string? Model, string? ApiKey)
{
    public static VlmEndpointConfig FromAppConfig(AppConfig config) =>
        new(config.VlmEndpoint, config.VlmModel, config.VlmApiKey);
}

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

    private static string GetPrompt(BlockAction action, PromptStyle style, bool structured)
    {
        // Structured mode: the response schema enforces shape at decode time,
        // but OpenAI's docs recommend repeating the expectation in the prompt
        // for best model behaviour.
        if (structured)
        {
            return action switch
            {
                BlockAction.Markdown =>
                    "Transcribe this table as a Markdown pipe table. Respond as JSON with a single `markdown` field.",
                BlockAction.Description =>
                    "Describe this figure in one concise sentence. Respond as JSON with a single `description` field.",
                _ =>
                    "Transcribe this equation as LaTeX (no delimiters, no $$). Respond as JSON with a single `latex` field.",
            };
        }

        return (action, style) switch
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
    }

    // Per-action response schemas for strict JSON schema mode.
    private static readonly Dictionary<BlockAction, (string FieldName, BinaryData Schema)> Schemas = new()
    {
        [BlockAction.LaTeX] = ("latex", BinaryData.FromString("""
            {"type":"object","properties":{"latex":{"type":"string"}},"required":["latex"],"additionalProperties":false}
            """)),
        [BlockAction.Markdown] = ("markdown", BinaryData.FromString("""
            {"type":"object","properties":{"markdown":{"type":"string"}},"required":["markdown"],"additionalProperties":false}
            """)),
        [BlockAction.Description] = ("description", BinaryData.FromString("""
            {"type":"object","properties":{"description":{"type":"string"}},"required":["description"],"additionalProperties":false}
            """)),
    };

    public static async Task<VlmResult> DescribeBlockAsync(
        byte[] pngBytes, BlockAction action, VlmEndpointConfig endpoint,
        PromptStyle style = PromptStyle.Instruction,
        bool structuredOutput = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Endpoint))
            return new VlmResult(null, "VLM not configured \u2014 check Settings");

        if (string.IsNullOrWhiteSpace(endpoint.Model))
            return new VlmResult(null, "VLM model not configured \u2014 check Settings");

        try
        {
            var client = CreateClient(endpoint);

            var imageContent = ChatMessageContentPart.CreateImagePart(
                BinaryData.FromBytes(pngBytes), "image/png");
            var textContent = ChatMessageContentPart.CreateTextPart(GetPrompt(action, style, structuredOutput));

            var messages = new List<ChatMessage>
            {
                new UserChatMessage(imageContent, textContent),
            };

            var chatOptions = new ChatCompletionOptions { MaxOutputTokenCount = 1024 };
            if (structuredOutput)
            {
                var (_, schema) = Schemas[action];
                chatOptions.ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: $"railreader_{action.ToString().ToLowerInvariant()}",
                    jsonSchema: schema,
                    jsonSchemaIsStrict: true);
            }

            var completion = await client.CompleteChatAsync(messages, chatOptions, ct);
            var text = completion.Value.Content[0].Text?.Trim();

            if (string.IsNullOrWhiteSpace(text))
                return new VlmResult(null, "VLM returned empty response");

            if (structuredOutput)
            {
                var (parsed, parseError) = ExtractSchemaField(text, Schemas[action].FieldName);
                if (parsed != null) return new VlmResult(parsed, null);
                // Parse failure: return raw text so the user can recover, but flag it.
                return new VlmResult(text, $"Structured parse failed: {parseError}");
            }

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
            var client = CreateClient(VlmEndpointConfig.FromAppConfig(config));

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

    private static (string? Value, string? Error) ExtractSchemaField(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, "response was not a JSON object");
            if (!doc.RootElement.TryGetProperty(field, out var valueElem))
                return (null, $"missing `{field}` field");
            if (valueElem.ValueKind != JsonValueKind.String)
                return (null, $"`{field}` was not a string");
            return (valueElem.GetString(), null);
        }
        catch (JsonException ex)
        {
            return (null, ex.Message);
        }
    }

    private static ChatClient CreateClient(VlmEndpointConfig endpoint)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint.Endpoint!),
        };
        // Local endpoints like Ollama don't require an API key
        var credential = new ApiKeyCredential(
            string.IsNullOrWhiteSpace(endpoint.ApiKey) ? "not-required" : endpoint.ApiKey);
        return new ChatClient(endpoint.Model!, credential, options);
    }
}
