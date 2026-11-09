using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace SonNuage.Server.Subsonic;

/// <summary>Codes d'erreur de l'API Subsonic (la réponse HTTP reste 200, <c>status="failed"</c>).</summary>
public enum SubsonicError
{
    Generic = 0,
    MissingParameter = 10,
    ClientTooOld = 20,
    ServerTooOld = 30,
    WrongCredentials = 40,
    TokenNotSupported = 41,
    AuthMechanismNotSupported = 42,
    ConflictingAuth = 43,
    InvalidApiKey = 44,
    NotAuthorized = 50,
    NotFound = 70,
}

/// <summary>
/// Élément d'une réponse Subsonic, écrit en XML (par défaut) ou en JSON (<c>f=json</c>) : attributs typés
/// (nombres, booléens, dates), enfants simples ou listes (toujours des tableaux en JSON, même vides), texte
/// (<c>value</c> en JSON). Les attributs nuls sont omis.
/// </summary>
public sealed class SubsonicNode
{
    public const string ApiVersion = "1.16.1";
    private const string Namespace = "http://subsonic.org/restapi";

    private readonly List<(string Key, object Value)> _attrs = [];
    private readonly List<SubsonicNode> _children = [];
    private readonly HashSet<string> _lists = [];
    private string? _text;

    private SubsonicNode(string name) => Name = name;

    public string Name { get; }

    public static SubsonicNode Ok(string serverVersion) => Root("ok", serverVersion);

    public static SubsonicNode Error(string serverVersion, SubsonicError code, string message)
    {
        var root = Root("failed", serverVersion);
        root.Add("error").Attr("code", (int)code).Attr("message", message);
        return root;
    }

    private static SubsonicNode Root(string status, string serverVersion) => new SubsonicNode("subsonic-response")
        .Attr("status", status).Attr("version", ApiVersion).Attr("type", "sonnuage").Attr("serverVersion", serverVersion)
        .Attr("openSubsonic", true);

    public SubsonicNode Attr(string key, object? value)
    {
        if (value is not null) _attrs.Add((key, value));
        return this;
    }

    public SubsonicNode Text(string? text)
    {
        _text = text;
        return this;
    }

    /// <summary>Enfant unique (objet en JSON).</summary>
    public SubsonicNode Add(string name)
    {
        var child = new SubsonicNode(name);
        _children.Add(child);
        return child;
    }

    /// <summary>Liste d'enfants nommés <paramref name="name"/> : un tableau en JSON, même vide.</summary>
    public SubsonicList List(string name)
    {
        _lists.Add(name);
        return new SubsonicList(this, name);
    }

    public readonly struct SubsonicList(SubsonicNode parent, string name)
    {
        public SubsonicNode Add() => parent.Add(name);
    }

    public string ToXml()
    {
        // Envoyé en UTF-8 : la déclaration est écrite à la main (un StringBuilder annoncerait UTF-16).
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = true, ConformanceLevel = ConformanceLevel.Fragment }))
        {
            WriteXml(w, isRoot: true);
        }
        return sb.ToString();
    }

    private void WriteXml(XmlWriter w, bool isRoot)
    {
        w.WriteStartElement(Name, Namespace);
        foreach (var (key, value) in _attrs) w.WriteAttributeString(key, Format(value));
        if (_text is not null) w.WriteString(_text);
        foreach (var child in _children) child.WriteXml(w, isRoot: false);
        w.WriteEndElement();
    }

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WritePropertyName(Name);
            WriteJson(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        foreach (var (key, value) in _attrs)
        {
            w.WritePropertyName(key);
            WriteJsonValue(w, value);
        }
        if (_text is not null) w.WriteString("value", _text);
        foreach (var name in _children.Select(c => c.Name).Concat(_lists).Distinct())
        {
            var group = _children.Where(c => c.Name == name).ToList();
            w.WritePropertyName(name);
            if (_lists.Contains(name))
            {
                w.WriteStartArray();
                foreach (var child in group) child.WriteJson(w);
                w.WriteEndArray();
            }
            else group[0].WriteJson(w);
        }
        w.WriteEndObject();
    }

    private static void WriteJsonValue(Utf8JsonWriter w, object value)
    {
        switch (value)
        {
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case double d: w.WriteNumberValue(d); break;
            case float f: w.WriteNumberValue(f); break;
            default: w.WriteStringValue(Format(value)); break;
        }
    }

    private static string Format(object value) => value switch
    {
        bool b => b ? "true" : "false",
        DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        DateTime d => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
