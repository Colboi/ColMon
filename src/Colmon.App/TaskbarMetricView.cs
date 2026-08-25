namespace Colmon;

internal interface ITaskbarMetricView
{
    Control Control { get; }
    string Title { get; set; }
    int CharacterColumns { get; }
    int LogicalHeight { get; }
    void SetSourceText(string text);
    object Snapshot(int characterCellWidth, int pixelWidth, int refreshIntervalSeconds);
}
