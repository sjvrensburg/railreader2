using System.CommandLine;
using RailReader.Cli;
using RailReader.Cli.Commands;
using RailReader.Cli.Output;

var jsonOption = new Option<bool>("--json") { Description = "Output in JSON format for machine consumption", Recursive = true };

var rootCommand = new RootCommand("railreader — CLI for RailReader2 PDF viewer");
rootCommand.Add(jsonOption);

rootCommand.Add(DocumentCommands.Create());
rootCommand.Add(TextCommands.Create());
rootCommand.Add(NavCommands.Create());
rootCommand.Add(AnalysisCommands.Create());
rootCommand.Add(AnnotationCommands.Create());
rootCommand.Add(BookmarkCommands.Create());
rootCommand.Add(ConfigCommands.Create());
rootCommand.Add(ExportCommands.Create());
rootCommand.Add(ReplCommand.Create(rootCommand));

// Initialize session before command invocation
using var session = new CliSession();
SessionBinder.Session = session;

// Determine formatter from parsed --json option
var parseResult = rootCommand.Parse(args);
bool jsonMode = parseResult.GetValue(jsonOption);
SessionBinder.Formatter = jsonMode ? new JsonFormatter() : new HumanFormatter();

return parseResult.Invoke();
