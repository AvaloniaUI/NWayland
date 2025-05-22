using System.IO;
using System.Xml.Serialization;

namespace NWayland.Generator
{
    [System.SerializableAttribute]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true)]
    [System.Xml.Serialization.XmlRootAttribute(Namespace = "", IsNullable = false, ElementName = "protocol")]
    public class WaylandProtocol
    {
        [System.Xml.Serialization.XmlElementAttribute("interface")]
        public WaylandProtocolInterface[] Interfaces { get; set; }

        [System.Xml.Serialization.XmlAttributeAttribute("name")]
        public string Name { get; set; }

        public static WaylandProtocol ParseXml(string xml)
        {
            return (WaylandProtocol)new XmlSerializer(typeof(WaylandProtocol)).Deserialize(new StringReader(xml));
        }
    }

    [System.SerializableAttribute]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true)]
    public class WaylandProtocolInterface
    {
        [System.Xml.Serialization.XmlElementAttribute("enum", typeof(WaylandProtocolEnum))]
        public WaylandProtocolEnum[]? Enums { get; set; }
        [System.Xml.Serialization.XmlElementAttribute("event", typeof(WaylandProtocolMessage))]
        public WaylandProtocolMessage[]? Events { get; set; }
        [System.Xml.Serialization.XmlElementAttribute("request", typeof(WaylandProtocolRequest))]
        public WaylandProtocolRequest[]? Requests { get; set; }

        [System.Xml.Serialization.XmlElementAttribute("description", typeof(WaylandProtocolDescription))]
        public WaylandProtocolDescription? Description { get; set; }

        [System.Xml.Serialization.XmlAttributeAttribute("name")]
        public string Name { get; set; }

        [System.Xml.Serialization.XmlAttributeAttribute("version")]
        public int Version { get; set; }
    }

    [System.SerializableAttribute]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true)]
    public class WaylandProtocolDescription
    {
        [System.Xml.Serialization.XmlAttributeAttribute("summary")]
        public string Summary { get; set; }

        [System.Xml.Serialization.XmlTextAttribute]
        public string Value { get; set; }
    }

    [System.SerializableAttribute]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true)]
    public class WaylandProtocolEnum
    {
        [System.Xml.Serialization.XmlElementAttribute("description", typeof(WaylandProtocolDescription))]
        public WaylandProtocolDescription Description { get; set; }

        [System.Xml.Serialization.XmlElementAttribute("entry")]
        public WaylandProtocolEnumEntry[] Entries { get; set; }

        [System.Xml.Serialization.XmlAttributeAttribute("name")]
        public string Name { get; set; }

        [System.Xml.Serialization.XmlAttributeAttribute("bitfield")]
        public bool IsBitField { get; set; }

        [System.Xml.Serialization.XmlAttributeAttribute("since")]
        public int Since { get; set; }
    }

    [System.SerializableAttribute]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true)]
    public class WaylandProtocolEnumEntry
    {
        [System.Xml.Serialization.XmlAttributeAttribute("name")]
        public string Name { get; set; }

        [System.Xml.Serialization.XmlAttributeAttribute("value")]
        public string Value { get; set; }

        [System.Xml.Serialization.XmlAttributeAttribute("summary")]
        public string Summary { get; set; }

        [System.Xml.Serialization.XmlAttributeAttribute("since")]
        public int Since { get; set; }

    }

    [System.SerializableAttribute]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true)]
    public class WaylandProtocolMessage
    {
        [System.Xml.Serialization.XmlElementAttribute("description", typeof(WaylandProtocolDescription))]
        public WaylandProtocolDescription Description { get; set; }

        [System.Xml.Serialization.XmlElementAttribute("arg")]
        public WaylandProtocolArgument[]? Arguments { get; set; }

        [System.Xml.Serialization.XmlAttributeAttribute("name")]
        public string Name { get; set; }

        [System.Xml.Serialization.XmlAttributeAttribute("since")]
        public int Since { get; set; }
    }

    [System.SerializableAttribute]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true)]
    public class WaylandProtocolRequest : WaylandProtocolMessage
    {
        [System.Xml.Serialization.XmlAttributeAttribute("type")]
        public string Type { get; set; }
    }

    [System.SerializableAttribute]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true)]
    public class WaylandProtocolArgument
    {
        [System.Xml.Serialization.XmlAttributeAttribute("name")]
        public string Name { get; set; }

        [System.Xml.Serialization.XmlAttributeAttribute("type")]
        public string Type { get; set; }

        [System.Xml.Serialization.XmlAttributeAttribute("summary")]
        public string Summary { get; set; }

        [System.Xml.Serialization.XmlAttributeAttribute("enum")]
        public string Enum { get; set; }

        [System.Xml.Serialization.XmlAttributeAttribute("allow-null")]
        public bool AllowNull { get; set; }

        [System.Xml.Serialization.XmlAttributeAttribute("interface")]
        public string? Interface { get; set; }
    }
}
