namespace Colmon;

internal interface ITaskbarMetricView
{
    Control Control { get; }
    string Title { get; set; }
    int CharacterColumns { get; }
    int LogicalHeight { get; }
    void SetSourceText(string text);
    void SetSourceSample(InfoSample sample);
    object Snapshot(int characterCellWidth, int pixelWidth, int refreshIntervalSeconds);
}
