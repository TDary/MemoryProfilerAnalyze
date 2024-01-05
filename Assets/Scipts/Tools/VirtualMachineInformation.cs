namespace Scipts.Tools
{
    internal struct VirtualMachineInformation
    {
        public uint PointerSize { get; internal set; }
        public uint ObjectHeaderSize { get; internal set; }
        public uint ArrayHeaderSize { get; internal set; }
        public uint ArrayBoundsOffsetInHeader { get; internal set; }
        public uint ArraySizeOffsetInHeader { get; internal set; }
        public uint AllocationGranularity { get; internal set; }
    }
}