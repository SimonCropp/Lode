/// <summary>
/// Every enum in the settings, kept by its name, as JsonStringEnumConverter keeps it, wherever it
/// is: a setting of its own, one in a list, or one that may be null. A name this Lode doesn't
/// know, one a newer Lode added and left in the file on going back a version, is read as the
/// enum's default, or null for one that may be null, as a setting missing from the file is read
/// as its default: thrown, it had the whole file set aside, and every tab, the code folder, the
/// pins and placements with it, over one value.
/// </summary>
sealed class LenientEnums : JsonConverterFactory
{
    public override bool CanConvert(Type type) =>
        type.IsEnum ||
        Nullable.GetUnderlyingType(type) is { IsEnum: true };

    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return (JsonConverter) Activator.CreateInstance(typeof(NullableNamed<>).MakeGenericType(underlying), options)!;
        }

        return (JsonConverter) Activator.CreateInstance(typeof(Named<>).MakeGenericType(type), options)!;
    }

    /// <summary>
    /// The value <paramref name="reader"/> is at: its name, or its number, as JsonStringEnumConverter
    /// reads one too. Null for one the enum hasn't, a name or number it doesn't know, or anything
    /// else, which is passed over whole.
    /// </summary>
    static T? Read<T>(ref Utf8JsonReader reader)
        where T : struct, Enum
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                // TryParse takes a number written as a name too, so one the enum hasn't is
                // looked for as well.
                if (Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var named) &&
                    Enum.IsDefined(named))
                {
                    return named;
                }

                return null;
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var number) &&
                    Enum.ToObject(typeof(T), number) is T numbered &&
                    Enum.IsDefined(numbered))
                {
                    return numbered;
                }

                return null;
            default:
                // A converter is left at the end of what it reads, a list or an object's too.
                reader.Skip();
                return null;
        }
    }

    // Written as JsonStringEnumConverter writes it, which every Lode before this one reads.
    static JsonConverter<T> Writer<T>(JsonSerializerOptions options)
        where T : struct, Enum =>
        (JsonConverter<T>) new JsonStringEnumConverter<T>().CreateConverter(typeof(T), options);

    sealed class Named<T>(JsonSerializerOptions options) : JsonConverter<T>
        where T : struct, Enum
    {
        JsonConverter<T> writer = Writer<T>(options);

        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            Read<T>(ref reader) ?? default;

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            this.writer.Write(writer, value, options);
    }

    sealed class NullableNamed<T>(JsonSerializerOptions options) : JsonConverter<T?>
        where T : struct, Enum
    {
        JsonConverter<T> writer = Writer<T>(options);

        // Given null too, so a null in the file is read as null, rather than passed over.
        public override bool HandleNull => true;

        public override T? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            return Read<T>(ref reader);
        }

        public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
        {
            if (value is not { } known)
            {
                writer.WriteNullValue();
                return;
            }

            this.writer.Write(writer, known, options);
        }
    }
}
