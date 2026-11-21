/// <summary>
/// The colours Lode's windows are drawn in: Windows' own choice of light or dark, or one of the
/// two whatever Windows has.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<Theme>))]
enum Theme
{
    System,
    Light,
    Dark
}
