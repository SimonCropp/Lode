/// <summary>
/// Whether an action shows as a button on the actions bar, in the right-click menu, or both, and
/// in each, whether under its parent.
/// </summary>
sealed class ActionPlacement
{
    public bool Button { get; set; }
    public bool Menu { get; set; }

    // Null takes LodeActions' default, as a placement saved before actions had parents does.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ButtonNested { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MenuNested { get; set; }
}