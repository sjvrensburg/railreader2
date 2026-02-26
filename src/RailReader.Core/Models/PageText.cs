namespace RailReader.Core.Models;

public record PageText(string Text, List<CharBox> CharBoxes);

public record struct CharBox(int Index, float Left, float Top, float Right, float Bottom);
