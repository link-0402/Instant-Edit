namespace InstantEdit.Models;

/// <summary>How Penumbra should refresh actors after applying an Instant Edit export.</summary>
public enum ExportRedrawMode
{
    /// <summary>Redraw only the actor from which the model was imported.</summary>
    Self,

    /// <summary>Redraw every currently available actor.</summary>
    All,

    /// <summary>Skip Penumbra redraw and allow Glamourer to refresh the actor.</summary>
    Glamourer,
}
