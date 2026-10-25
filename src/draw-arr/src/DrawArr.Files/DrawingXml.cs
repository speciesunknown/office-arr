using System.Globalization;
using System.Text;
using System.Xml;
using DrawArr.Core;

namespace DrawArr.Files;

/// <summary>Drawings to and from draw-arr's XML format, read strictly because files may come from anyone.</summary>
public static class DrawingXml
{
    /// <summary>The namespace every element of a drawing is in.</summary>
    public const string Namespace = "urn:office-arr:draw-arr";

    /// <summary>The format version this draw-arr writes and reads.</summary>
    public const int Version = 1;

    /// <summary>The most characters a drawing file may hold, above the about 62 million a full drawing of 1000-character texts needs when every character is escaped as <c>&amp;quot;</c>.</summary>
    public const long MaxCharacters = 64 * 1024 * 1024;

    // Stored names are spelled out here rather than taken from the enums, so renaming a member can't change what files mean.
    private static readonly Dictionary<Colour, string> ColourNames = new()
    {
        [Colour.White] = "white",
        [Colour.Red] = "red",
        [Colour.Yellow] = "yellow",
        [Colour.Blue] = "blue",
        [Colour.Green] = "green",
    };

    private static readonly Dictionary<Edge, string> EdgeNames = new()
    {
        [Edge.Top] = "top",
        [Edge.Right] = "right",
        [Edge.Bottom] = "bottom",
        [Edge.Left] = "left",
    };

    private static readonly Dictionary<string, Colour> Colours = ColourNames.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
    private static readonly Dictionary<string, Edge> Edges = EdgeNames.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    /// <summary>Writes a drawing as UTF-8 XML.</summary>
    public static void Write(Drawing drawing, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        ArgumentNullException.ThrowIfNull(stream);
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), CloseOutput = false };
        using var xml = XmlWriter.Create(stream, settings);
        xml.WriteStartDocument();
        xml.WriteStartElement("drawing", Namespace);
        xml.WriteAttributeString("version", Number(Version));
        foreach (var box in drawing.Boxes.Values)
        {
            xml.WriteStartElement("box", Namespace);
            xml.WriteAttributeString("id", Number(box.Id));
            xml.WriteAttributeString("x", Number(box.Bounds.X));
            xml.WriteAttributeString("y", Number(box.Bounds.Y));
            xml.WriteAttributeString("width", Number(box.Bounds.Width));
            xml.WriteAttributeString("height", Number(box.Bounds.Height));
            xml.WriteAttributeString("colour", ColourNames[box.Colour]);
            if (box.Text.Length > 0)
                xml.WriteAttributeString("text", box.Text);
            xml.WriteEndElement();
        }
        foreach (var line in drawing.Lines.Values)
        {
            xml.WriteStartElement("line", Namespace);
            xml.WriteAttributeString("id", Number(line.Id));
            xml.WriteAttributeString("colour", ColourNames[line.Colour]);
            WriteEnd(xml, "start", line.Start);
            WriteEnd(xml, "end", line.End);
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    /// <summary>Reads a drawing, or an error saying what is wrong with the file and where.</summary>
    public static Result<Drawing> Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var settings = new XmlReaderSettings
        {
            // No DTDs means no entity expansion and nothing fetched from elsewhere.
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCharacters,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            CloseInput = false,
        };
        try
        {
            using var xml = XmlReader.Create(stream, settings);
            return new Parser(xml).Drawing();
        }
        catch (XmlException e)
        {
            // The reader throws for anything that isn't well-formed XML, which is the file's fault.
            return Result<Drawing>.Fail(ErrorCode.FileInvalid, $"The file isn't valid XML: {e.Message}");
        }
    }

    private static void WriteEnd(XmlWriter xml, string name, LineEnd end)
    {
        xml.WriteStartElement(name, Namespace);
        switch (end)
        {
            case LineEnd.Free free:
                xml.WriteAttributeString("x", Number(free.At.X));
                xml.WriteAttributeString("y", Number(free.At.Y));
                break;
            case LineEnd.Attached attached:
                xml.WriteAttributeString("box", Number(attached.BoxId));
                xml.WriteAttributeString("edge", EdgeNames[attached.Edge]);
                xml.WriteAttributeString("offset", Number(attached.Offset));
                break;
            default:
                throw new InvalidOperationException($"Unhandled line end {end.GetType().Name}.");
        }
        xml.WriteEndElement();
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>One pass over a reader, keeping what it has found so far.</summary>
    private sealed class Parser(XmlReader xml)
    {
        private readonly List<Box> _boxes = [];
        private readonly List<(Line Line, int LineNumber)> _lines = [];
        private readonly HashSet<long> _ids = [];

        public Result<Drawing> Drawing()
        {
            if (xml.MoveToContent() != XmlNodeType.Element || !Is("drawing"))
                return Fail("This isn't a draw-arr drawing.");
            var version = xml.GetAttribute("version");
            if (version != Number(Version))
            {
                return int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > Version
                    ? Fail("This drawing was saved by a newer draw-arr.")
                    : Fail("This drawing has no version draw-arr knows.");
            }
            // Checked after the version, so a newer file is reported as newer rather than as having strange attributes.
            Element = "drawing";
            var knownRoot = Known("version");
            if (knownRoot.IsFailure)
                return Result<Drawing>.Fail(knownRoot.Error);

            if (!xml.IsEmptyElement)
            {
                xml.Read();
                while (xml.NodeType != XmlNodeType.EndElement)
                {
                    var item = Item();
                    if (item.IsFailure)
                        return Result<Drawing>.Fail(item.Error);
                }
            }
            // Reading on to the end makes the reader check nothing but comments follow the drawing.
            while (xml.Read())
            {
            }
            return Build();
        }

        /// <summary>An error naming the first attribute of the current element that isn't one of the expected ones, ignoring namespace declarations and other namespaces.</summary>
        private Result Known(params string[] names)
        {
            if (xml.MoveToFirstAttribute())
            {
                try
                {
                    do
                    {
                        if (xml.Prefix == "xmlns" || xml.Name == "xmlns" || xml.NamespaceURI.Length > 0)
                            continue;
                        if (Array.IndexOf(names, xml.LocalName) < 0)
                            return Fail($"'{xml.LocalName}' isn't an attribute draw-arr knows for '{Element}'.").ToResult();
                    }
                    while (xml.MoveToNextAttribute());
                }
                finally
                {
                    xml.MoveToElement();
                }
            }
            return Result.Ok();
        }

        private string Element { get; set; } = "";

        private Result Item()
        {
            if (xml.NodeType != XmlNodeType.Element)
                return Fail("Only boxes and lines can be inside a drawing.").ToResult();
            if (_boxes.Count + _lines.Count >= Core.Drawing.MaxItems)
                return Fail($"A drawing can hold at most {Core.Drawing.MaxItems} boxes and lines.").ToResult();
            if (Is("box"))
                return Checked("box", ["id", "x", "y", "width", "height", "colour", "text"], Box);
            if (Is("line"))
                return Checked("line", ["id", "colour"], Line);
            return Fail($"'{xml.Name}' isn't something draw-arr knows.").ToResult();
        }

        private Result Box()
        {
            var id = Id();
            if (id.IsFailure)
                return id.ToResult();
            var x = Int("x");
            var y = Int("y");
            var width = Int("width");
            var height = Int("height");
            foreach (var part in new[] { x, y, width, height })
            {
                if (part.IsFailure)
                    return part.ToResult();
            }
            var colour = Named("colour", Colours);
            if (colour.IsFailure)
                return colour.ToResult();
            var text = BoxText.Validate(xml.GetAttribute("text") ?? "");
            if (text.IsFailure)
                return Fail(text.Error.Message).ToResult();

            var box = Core.Box.Create(id.Value, new Rect(x.Value, y.Value, width.Value, height.Value), colour.Value, text.Value);
            if (box.IsFailure)
                return Fail(box.Error.Message).ToResult();
            _boxes.Add(box.Value);
            return Empty("box");
        }

        private Result Line()
        {
            var lineNumber = LineNumber;
            var id = Id();
            if (id.IsFailure)
                return id.ToResult();
            var colour = Named("colour", Colours);
            if (colour.IsFailure)
                return colour.ToResult();
            if (xml.IsEmptyElement)
                return Fail($"Line {id.Value} needs a start and an end.").ToResult();

            xml.Read();
            var start = End("start");
            if (start.IsFailure)
                return start.ToResult();
            var end = End("end");
            if (end.IsFailure)
                return end.ToResult();
            if (xml.NodeType != XmlNodeType.EndElement)
                return Fail($"Line {id.Value} can only have a start and an end.").ToResult();
            xml.Read();

            var line = Core.Line.Create(id.Value, start.Value, end.Value, colour.Value);
            if (line.IsFailure)
                return Fail(line.Error.Message).ToResult();
            _lines.Add((line.Value, lineNumber));
            return Result.Ok();
        }

        /// <summary>Reads an element once its attributes are known to be expected ones.</summary>
        private Result Checked(string element, string[] attributes, Func<Result> read)
        {
            Element = element;
            var known = Known(attributes);
            return known.IsSuccess ? read() : known;
        }

        private Result<LineEnd> End(string name)
        {
            if (xml.NodeType != XmlNodeType.Element || !Is(name))
                return Result<LineEnd>.Fail(ErrorCode.FileInvalid, At($"A line's {name} is missing."));
            Element = name;
            var known = Known(xml.GetAttribute("box") is null ? ["x", "y"] : ["box", "edge", "offset"]);
            if (known.IsFailure)
                return Result<LineEnd>.Fail(known.Error);
            var end = xml.GetAttribute("box") is null ? FreeEnd() : AttachedEnd();
            if (end.IsFailure)
                return end;
            var closed = Empty(name);
            return closed.IsSuccess ? end : Result<LineEnd>.Fail(closed.Error);
        }

        private Result<LineEnd> FreeEnd()
        {
            var x = Int("x");
            if (x.IsFailure)
                return Result<LineEnd>.Fail(x.Error);
            var y = Int("y");
            if (y.IsFailure)
                return Result<LineEnd>.Fail(y.Error);
            var free = LineEnd.Free.Create(new Point(x.Value, y.Value));
            return free.IsSuccess ? free.Value : Result<LineEnd>.Fail(ErrorCode.FileInvalid, At(free.Error.Message));
        }

        private Result<LineEnd> AttachedEnd()
        {
            var box = Long("box");
            if (box.IsFailure)
                return Result<LineEnd>.Fail(box.Error);
            var edge = Named("edge", Edges);
            if (edge.IsFailure)
                return Result<LineEnd>.Fail(edge.Error);
            var offset = Int("offset");
            if (offset.IsFailure)
                return Result<LineEnd>.Fail(offset.Error);
            var attached = LineEnd.Attached.Create(box.Value, edge.Value, offset.Value);
            return attached.IsSuccess ? attached.Value : Result<LineEnd>.Fail(ErrorCode.FileInvalid, At(attached.Error.Message));
        }

        /// <summary>Checks an element has no content and moves past it.</summary>
        private Result Empty(string name)
        {
            if (xml.IsEmptyElement)
            {
                xml.Read();
                return Result.Ok();
            }
            xml.Read();
            if (xml.NodeType != XmlNodeType.EndElement)
                return Fail($"A {name} can't have anything inside it.").ToResult();
            xml.Read();
            return Result.Ok();
        }

        private Result<Drawing> Build()
        {
            var drawing = Core.Drawing.Empty;
            foreach (var box in _boxes)
                drawing = drawing.With(box);
            foreach (var (line, lineNumber) in _lines)
            {
                // An offset past the end of its edge is allowed, because shrinking a box keeps offsets so growing it again restores them.
                foreach (var end in new[] { line.Start, line.End })
                {
                    if (end is LineEnd.Attached attached && !drawing.Boxes.ContainsKey(attached.BoxId))
                        return Result<Drawing>.Fail(ErrorCode.FileInvalid, $"Line {lineNumber}: line {line.Id} is attached to {attached.BoxId}, which isn't a box in the file.");
                }
                drawing = drawing.With(line);
            }
            return drawing;
        }

        private bool Is(string localName) => xml.LocalName == localName && xml.NamespaceURI == Namespace;

        private int LineNumber => xml is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

        private string At(string message) => $"Line {LineNumber}: {message}";

        private Result<Drawing> Fail(string message) => Result<Drawing>.Fail(ErrorCode.FileInvalid, At(message));

        private Result<long> Id()
        {
            var id = Long("id");
            if (id.IsFailure)
                return id;
            if (id.Value <= 0 || id.Value > Core.Drawing.MaxId)
                return Result<long>.Fail(ErrorCode.FileInvalid, At($"Id {id.Value} isn't between 1 and {Core.Drawing.MaxId}."));
            if (!_ids.Add(id.Value))
                return Result<long>.Fail(ErrorCode.FileInvalid, At($"Id {id.Value} is used twice."));
            return id;
        }

        private Result<long> Long(string name)
        {
            var text = xml.GetAttribute(name);
            if (text is null)
                return Result<long>.Fail(ErrorCode.FileInvalid, At($"'{xml.LocalName}' needs a '{name}'."));
            return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? value
                : Result<long>.Fail(ErrorCode.FileInvalid, At($"'{name}' must be a whole number."));
        }

        private Result<int> Int(string name)
        {
            var value = Long(name);
            if (value.IsFailure)
                return Result<int>.Fail(value.Error);
            return value.Value is >= int.MinValue and <= int.MaxValue
                ? (int)value.Value
                : Result<int>.Fail(ErrorCode.FileInvalid, At($"'{name}' is out of range."));
        }

        private Result<T> Named<T>(string name, Dictionary<string, T> names) where T : struct
        {
            var text = xml.GetAttribute(name);
            if (text is null)
                return Result<T>.Fail(ErrorCode.FileInvalid, At($"'{xml.LocalName}' needs a '{name}'."));
            return names.TryGetValue(text, out var value)
                ? value
                : Result<T>.Fail(ErrorCode.FileInvalid, At($"'{name}' must be one of: {string.Join(", ", names.Keys)}."));
        }
    }
}
