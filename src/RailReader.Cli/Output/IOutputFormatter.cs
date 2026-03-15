namespace RailReader.Cli.Output;

public interface IOutputFormatter
{
    void WriteResult(object result);
    void WriteError(string message);
    void WriteMessage(string message);
}
