using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace AnthropicSpike;

/// <summary>
/// The C# twin of review_model.py / extraction_model.py: builds the same
/// Messages request and the same client (240 s timeout, one retry).
/// Only request construction and result classification are spiked; the
/// tests inject an HttpClient so nothing leaves the process.
/// </summary>
public static class ModelAdapters
{
    public const string ReviewModel = "claude-opus-5";
    public const long MaxOutputTokens = 16000;
    public static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(240);
    public const int ClientMaxRetries = 1;

    public const string ExtractionInstruction =
        "Extract this document into the required output format, following your"
        + " instructions exactly.";

    public static AnthropicClient NewClient(string apiKey, HttpClient? http = null) =>
        new()
        {
            ApiKey = apiKey,
            Timeout = ClientTimeout,
            MaxRetries = ClientMaxRetries,
            HttpClient = http ?? new HttpClient(),
        };

    /// <summary>output_config.format = {type: json_schema, schema}.</summary>
    private static OutputConfig JsonSchemaOutput(JsonElement schema)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var p in schema.EnumerateObject())
        {
            dict[p.Name] = p.Value.Clone();
        }
        return new OutputConfig { Format = new JsonOutputFormat { Schema = dict } };
    }

    public static MessageCreateParams ReviewRequest(
        string document, string systemPrompt, JsonElement schema, string model = ReviewModel) =>
        new()
        {
            Model = model,
            MaxTokens = MaxOutputTokens,
            System = systemPrompt,
            Messages = [new UserMessageParam { Content = document }],
            OutputConfig = JsonSchemaOutput(schema),
        };

    public static MessageCreateParams ExtractionRequest(
        string mediaType, byte[] data, string systemPrompt, JsonElement schema,
        string model = ReviewModel)
    {
        var b64 = Convert.ToBase64String(data);
        ContentBlockParam block = mediaType == "application/pdf"
            ? new DocumentBlockParam
            {
                Source = new Base64PdfSource { Data = b64 },
            }
            : new ImageBlockParam
            {
                Source = new Base64ImageSource
                {
                    Data = b64,
                    MediaType = mediaType switch
                    {
                        "image/png" => MediaType.ImagePng,
                        "image/jpeg" => MediaType.ImageJpeg,
                        _ => throw new ArgumentException(mediaType),
                    },
                },
            };
        return new MessageCreateParams
        {
            Model = model,
            MaxTokens = MaxOutputTokens,
            System = systemPrompt,
            Messages =
            [
                new UserMessageParam
                {
                    Content = new List<ContentBlockParam>
                    {
                        block,
                        new TextBlockParam { Text = ExtractionInstruction },
                    },
                },
            ],
            OutputConfig = JsonSchemaOutput(schema),
        };
    }
}
