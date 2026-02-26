using Microsoft.Extensions.AI;
using OpenAI;
using RailReader.Core;
using RailReader.Core.Services;
using RailReader.Agent;

// --- Configuration ---
var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
    ?? throw new InvalidOperationException(
        "Set OPENAI_API_KEY or ANTHROPIC_API_KEY environment variable");

var modelId = Environment.GetEnvironmentVariable("RAILREADER_MODEL") ?? "gpt-4o";
var baseUrl = Environment.GetEnvironmentVariable("RAILREADER_BASE_URL");

// --- Setup ---
var config = AppConfig.Load();
var controller = new DocumentController(config, new SynchronousThreadMarshaller());
controller.SetViewportSize(1200, 900); // virtual viewport for headless use

// Try to initialize the ONNX worker (optional for agent use)
try { controller.InitializeWorker(); }
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine($"Warning: {ex.Message}");
    Console.Error.WriteLine("Layout analysis will not be available.");
}

var tools = new RailReaderTools(controller);

// --- Build AI tools ---
var aiTools = new List<AITool>
{
    AIFunctionFactory.Create(tools.OpenDocument),
    AIFunctionFactory.Create(tools.ListDocuments),
    AIFunctionFactory.Create(tools.GetActiveDocument),
    AIFunctionFactory.Create(tools.GoToPage),
    AIFunctionFactory.Create(tools.NextPage),
    AIFunctionFactory.Create(tools.PrevPage),
    AIFunctionFactory.Create(tools.GetPageText),
    AIFunctionFactory.Create(tools.GetLayoutInfo),
    AIFunctionFactory.Create(tools.Search),
    AIFunctionFactory.Create(tools.CloseDocument),
    AIFunctionFactory.Create(tools.SetZoom),
    AIFunctionFactory.Create(tools.AddHighlight),
    AIFunctionFactory.Create(tools.AddTextAnnotation),
    AIFunctionFactory.Create(tools.ExportPdf),
};

// --- Build chat client ---
var openAiClient = baseUrl is not null
    ? new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey),
        new OpenAIClientOptions { Endpoint = new Uri(baseUrl) })
    : new OpenAIClient(apiKey);

IChatClient client = openAiClient.GetChatClient(modelId)
    .AsIChatClient()
    .AsBuilder()
    .UseFunctionInvocation()
    .Build();

// --- Get task from args or stdin ---
string task;
if (args.Length > 0)
    task = string.Join(" ", args);
else
{
    Console.Write("Enter task: ");
    task = Console.ReadLine() ?? "";
}

if (string.IsNullOrWhiteSpace(task))
{
    Console.Error.WriteLine("No task provided.");
    return 1;
}

// --- Run agent loop ---
var messages = new List<ChatMessage>
{
    new(ChatRole.System, """
        You are a PDF reading assistant with access to RailReader tools.
        You can open PDFs, navigate pages, extract text, search, annotate, and export.
        Use the tools to accomplish the user's task, then report your findings.
        """),
    new(ChatRole.User, task),
};

var options = new ChatOptions { Tools = aiTools };

var response = await client.GetResponseAsync(messages, options);
Console.WriteLine(response.Text);

// --- Cleanup ---
foreach (var doc in controller.Documents.ToList())
    doc.Dispose();

return 0;
