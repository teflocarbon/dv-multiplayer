using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DvmpServer;

/// <summary>
/// The server's Multiplayer settings: fields of Multiplayer/Settings.cs in the Settings.xml Unity
/// Mod Manager keeps in the mod's folder (XmlSerializer's form of it, root element "Settings").
/// Only these fields are touched; everything else in the file is kept as it is. A field missing
/// from the file takes the mod's default when the game loads it.
/// </summary>
public class ServerSettings
{
    public string ServerName = "";
    public string Password = "";
    public int MaxPlayers = 4;
    /// <summary>Multiplayer's ServerVisibility: Private, Friends or Public.</summary>
    public string Visibility = "Public";
    public int Port = 7777;
    public string Details = "";
    /// <summary>SteamID64s (or player GUIDs), separated by commas.</summary>
    public string Admins = "";
    public bool DebugLogging;

    public static readonly string[] Visibilities = { "Private", "Friends", "Public" };

    public static ServerSettings Load(string path)
    {
        var settings = new ServerSettings();
        if (path == null || !File.Exists(path))
            return settings;
        XElement root;
        try { root = XDocument.Load(path).Root; }
        catch (Exception) { return settings; }
        if (root == null)
            return settings;
        string Get(string name) => root.Element(name)?.Value;
        settings.ServerName = Get("ServerName") ?? settings.ServerName;
        settings.Password = Get("Password") ?? settings.Password;
        if (int.TryParse(Get("MaxPlayers"), out int max)) settings.MaxPlayers = max;
        string visibility = Get("Visibility");
        if (visibility != null && Visibilities.Contains(visibility)) settings.Visibility = visibility;
        if (int.TryParse(Get("Port"), out int port)) settings.Port = port;
        settings.Details = Get("Details") ?? settings.Details;
        settings.Admins = Get("Admins") ?? settings.Admins;
        if (bool.TryParse(Get("DebugLogging"), out bool debug)) settings.DebugLogging = debug;
        return settings;
    }

    public void Save(string path)
    {
        XDocument doc = null;
        if (File.Exists(path))
        {
            try { doc = XDocument.Load(path); }
            catch (Exception) { doc = null; }
        }
        if (doc?.Root == null)
        {
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            XNamespace xsd = "http://www.w3.org/2001/XMLSchema";
            doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
                new XElement("Settings",
                    new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "xsd", xsd.NamespaceName)));
        }
        XElement root = doc.Root;
        void Set(string name, string value)
        {
            XElement element = root.Element(name);
            if (element == null)
                root.Add(new XElement(name, value));
            else
                element.Value = value;
        }
        Set("ServerName", ServerName ?? "");
        Set("Password", (Password ?? "").Trim());
        Set("MaxPlayers", MaxPlayers.ToString());
        Set("Visibility", Visibility);
        // Older settings files: PublicGame came before Visibility (Settings.MigrateSettings).
        if (root.Element("PublicGame") != null)
            Set("PublicGame", Visibility == "Public" ? "true" : "false");
        Set("Port", Port.ToString());
        Set("Details", Details ?? "");
        Set("Admins", Admins ?? "");
        Set("DebugLogging", DebugLogging ? "true" : "false");

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var xmlSettings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using (XmlWriter writer = XmlWriter.Create(path, xmlSettings))
            doc.Save(writer);
    }
}
