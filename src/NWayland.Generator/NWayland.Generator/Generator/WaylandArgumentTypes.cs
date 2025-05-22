using System.Collections.Generic;

namespace NWayland.Generator
{
    static class WaylandArgumentTypes
    {
        public const string Int32 = "int";
        public const string Uint32 = "uint";
        public const string Fixed = "fixed";
        public const string String = "string";
        public const string Object = "object";
        public const string NewId = "new_id";
        public const string Array = "array";
        public const string FileDescriptor = "fd";

        public static readonly Dictionary<string, char> NamesToCodes = new()
        {
            ["int"] = 'i',
            ["uint"] = 'u',
            ["fixed"] = 'f',
            ["string"] = 's',
            ["object"] = 'o',
            ["new_id"] = 'n',
            ["array"] = 'a',
            ["fd"] = 'h'
        };
    }
}
