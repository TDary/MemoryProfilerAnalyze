namespace MemoryAnalyze
{
    public struct PackedManagedStaticField
    {
        // The index into PackedMemorySnapshot.typeDescriptions of the type this field belongs to.
        public System.Int32 managedTypesArrayIndex;

        // The index into the typeDescription.fields array
        public System.Int32 fieldIndex;

        // The index into the PackedMemorySnapshot.staticFields array
        public System.Int32 staticFieldsArrayIndex;
    }
}
