namespace Salvo.Core.Models;

public sealed class Configuration
{
    public int Version { get; set; } = 1;

    public List<Group> Groups { get; set; } = [];

    /// <summary>
    /// Identifier of the group designated as the system Boot Sequence.
    /// The Boot Sequence is just a regular <see cref="Group"/> whose
    /// nodes are typically <c>GroupCallNode</c> entries that orchestrate
    /// other groups in order with waits between. <c>null</c> means no
    /// boot sequence is configured yet.
    /// </summary>
    public string? BootSequenceGroupId { get; set; }
}
