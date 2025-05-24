using System;

namespace NWayland.Generator;

class NwGeneratorException : Exception
{
    public NwGeneratorException(string error) : base(error)
    {
        
    }
}
class NwGeneratorWithFileException : NwGeneratorException
{
    public string File { get; }

    public NwGeneratorWithFileException(string file, string error) : base($"{error} at {file}")
    {
        File = file;
    }
}