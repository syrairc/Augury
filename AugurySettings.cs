using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

namespace Augury;

public class AugurySettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    public bool ShowWindow = true;
    public int PollMs = 500;
    public bool LogToFile = true;
    public int MaxLogLines = 1000;
}
