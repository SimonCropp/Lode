using System.Globalization;

/// <summary>
/// Reads an SVG path's data, its d attribute, into a GraphicsPath: moves, lines, cubic curves and
/// closes, absolute or relative, which is all the outlines Lode draws use.
/// </summary>
static partial class SvgPath
{
    // A command letter, or a number. Numbers need no separator where a sign or a second decimal
    // point starts the next one: "-.3-1" is -0.3 and -1, "2.4.5" is 2.4 and 0.5.
    [GeneratedRegex(@"[A-Za-z]|-?(?:\d+(?:\.\d*)?|\.\d+)")]
    private static partial Regex Token();

    public static GraphicsPath Parse(string data)
    {
        var tokens = Token().Matches(data).Select(_ => _.Value).ToList();
        var path = new GraphicsPath();
        var index = 0;
        var command = 'M';
        var current = PointF.Empty;
        var start = PointF.Empty;

        float Number() =>
            float.Parse(tokens[index++], CultureInfo.InvariantCulture);

        // A relative point is from where the segment starts, so every point of a curve is from
        // the same place.
        PointF Point(bool relative)
        {
            var x = Number();
            var y = Number();
            if (relative)
            {
                return new(current.X + x, current.Y + y);
            }

            return new(x, y);
        }

        void LineTo(PointF to)
        {
            path.AddLine(current, to);
            current = to;
        }

        while (index < tokens.Count)
        {
            if (char.IsLetter(tokens[index][0]))
            {
                command = tokens[index][0];
                index++;
            }

            var relative = char.IsLower(command);
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    path.StartFigure();
                    current = Point(relative);
                    start = current;
                    // The pairs after a move's first are lines.
                    command = relative ? 'l' : 'L';
                    break;
                case 'L':
                    LineTo(Point(relative));
                    break;
                case 'H':
                    var x = Number();
                    LineTo(new(relative ? current.X + x : x, current.Y));
                    break;
                case 'V':
                    var y = Number();
                    LineTo(new(current.X, relative ? current.Y + y : y));
                    break;
                case 'C':
                    var control1 = Point(relative);
                    var control2 = Point(relative);
                    var end = Point(relative);
                    path.AddBezier(current, control1, control2, end);
                    current = end;
                    break;
                case 'Z':
                    path.CloseFigure();
                    current = start;
                    // Takes no numbers, so any before the next command are malformed, rather
                    // than read as another close for ever.
                    command = '\0';
                    break;
                case '\0':
                    throw new FormatException($"SVG path number {tokens[index]} after a close");
                default:
                    throw new NotSupportedException($"SVG path command {command}");
            }
        }

        return path;
    }
}
