using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Il2CppDumper
{
    // Metadata v39 (Unity 6000.3+) reader.
    // Kept apart from the older versions so that the existing code path is untouched.
    //
    // v39 differences handled here:
    //  - header: every table is an (offset, size, count) triple
    //  - TypeDefinitionIndex / GenericContainerIndex / TypeIndex fields have a variable width
    //    (1, 2 or 4 bytes depending on the size of the table they point into)
    //  - Il2CppTypeDefinition.elementTypeIndex was removed
    //  - Il2CppStringLiteral is only an offset (sentinel terminated)
    //  - Il2CppAssemblyDefinition has 4 additional bytes
    public sealed partial class Metadata
    {
        private static readonly string[] v39HeaderTables =
        {
            "stringLiteral", "stringLiteralData", "string", "events", "properties", "methods",
            "parameterDefaultValues", "fieldDefaultValues", "fieldAndParameterDefaultValueData",
            "fieldMarshaledSizes", "parameters", "fields", "genericParameters",
            "genericParameterConstraints", "genericContainers", "nestedTypes", "interfaces",
            "vtableMethods", "interfaceOffsets", "typeDefinitions", "images", "assemblies",
            "fieldRefs", "referencedAssemblies", "attributeData", "attributeDataRange",
            "unresolvedVirtualCallParameterTypes", "unresolvedVirtualCallParameterRanges",
            "windowsRuntimeTypeNames", "windowsRuntimeStrings", "exportedTypeDefinitions",
        };

        private readonly Dictionary<string, int> v39Counts = new();
        // Struct sizes that differ from what the Version attributes describe.
        private readonly Dictionary<Type, int> v39Sizes = new();
        private int typeDefIndexSize = 4;
        private int genericContainerIndexSize = 4;
        private int typeIndexSize = 4;
        private int parameterIndexSize = 4;

        private void InitV39()
        {
            ReadHeaderV39();
            v39Sizes[typeof(Il2CppTypeDefinition)] = header.typeDefinitionsSize / v39Counts["typeDefinitions"];
            v39Sizes[typeof(Il2CppGenericParameter)] = header.genericParametersSize / v39Counts["genericParameters"];

            static int IndexSize(int count) => count < byte.MaxValue ? 1 : count < ushort.MaxValue ? 2 : 4;
            typeDefIndexSize = IndexSize(v39Counts["typeDefinitions"]);
            genericContainerIndexSize = IndexSize(v39Counts["genericContainers"]);
            parameterIndexSize = IndexSize(v39Counts["parameters"]);
            var ifaceOffsetPairSize = v39Counts["interfaceOffsets"] == 0 ? 8 : header.interfaceOffsetsSize / v39Counts["interfaceOffsets"];
            typeIndexSize = ifaceOffsetPairSize switch { 5 => 1, 6 => 2, _ => 4 };

            imageDefs = ReadTableV39(header.imagesOffset, header.imagesSize, "images", ReadImageDefinitionV39);
            assemblyDefs = ReadTableV39(header.assembliesOffset, header.assembliesSize, "assemblies", ReadAssemblyDefinitionV39);
            typeDefs = ReadTableV39(header.typeDefinitionsOffset, header.typeDefinitionsSize, "typeDefinitions", ReadTypeDefinitionV39);
            methodDefs = ReadTableV39(header.methodsOffset, header.methodsSize, "methods", ReadMethodDefinitionV39);
            parameterDefs = ReadFixedTableV39<Il2CppParameterDefinition>(header.parametersOffset, header.parametersSize, "parameters");
            fieldDefs = ReadFixedTableV39<Il2CppFieldDefinition>(header.fieldsOffset, header.fieldsSize, "fields");
            var fieldDefaultValues = ReadFixedTableV39<Il2CppFieldDefaultValue>(header.fieldDefaultValuesOffset, header.fieldDefaultValuesSize, "fieldDefaultValues");
            var parameterDefaultValues = ReadFixedTableV39<Il2CppParameterDefaultValue>(header.parameterDefaultValuesOffset, header.parameterDefaultValuesSize, "parameterDefaultValues");
            fieldDefaultValuesDic = fieldDefaultValues.ToDictionary(x => x.fieldIndex);
            parameterDefaultValuesDic = parameterDefaultValues.ToDictionary(x => x.parameterIndex);
            propertyDefs = ReadFixedTableV39<Il2CppPropertyDefinition>(header.propertiesOffset, header.propertiesSize, "properties");
            interfaceIndices = ReadIndexArrayV39(header.interfacesOffset, header.interfacesSize, "interfaces");
            nestedTypeIndices = ReadIndexArrayV39(header.nestedTypesOffset, header.nestedTypesSize, "nestedTypes");
            eventDefs = ReadFixedTableV39<Il2CppEventDefinition>(header.eventsOffset, header.eventsSize, "events");
            genericContainers = ReadFixedTableV39<Il2CppGenericContainer>(header.genericContainersOffset, header.genericContainersSize, "genericContainers");
            genericParameters = ReadTableV39(header.genericParametersOffset, header.genericParametersSize, "genericParameters", ReadGenericParameterV39);
            constraintIndices = ReadIndexArrayV39(header.genericParameterConstraintsOffset, header.genericParameterConstraintsSize, "genericParameterConstraints");
            vtableMethods = ReadClassArray<uint>(header.vtableMethodsOffset, header.vtableMethodsSize / 4);
            stringLiterals = ReadStringLiteralsV39();
            fieldRefs = ReadFixedTableV39<Il2CppFieldRef>(header.fieldRefsOffset, header.fieldRefsSize, "fieldRefs");
            attributeDataRanges = ReadFixedTableV39<Il2CppCustomAttributeDataRange>(header.attributeDataRangeOffset, header.attributeDataRangeSize, "attributeDataRange");
            attributeTypeRangesDic = new Dictionary<Il2CppImageDefinition, Dictionary<uint, int>>();
            foreach (var imageDef in imageDefs)
            {
                var dic = new Dictionary<uint, int>();
                attributeTypeRangesDic[imageDef] = dic;
                var end = imageDef.customAttributeStart + imageDef.customAttributeCount;
                for (int i = imageDef.customAttributeStart; i < end; i++)
                {
                    dic.Add(attributeDataRanges[i].token, i);
                }
            }
            RestoreEnumElementTypesV39();
        }

        private void ReadHeaderV39()
        {
            Position = 8;
            header = new Il2CppGlobalMetadataHeader { sanity = 0xFAB11BAF, version = (int)Version };
            var type = typeof(Il2CppGlobalMetadataHeader);
            foreach (var name in v39HeaderTables)
            {
                var offset = ReadInt32();
                var size = ReadInt32();
                var count = ReadInt32();
                v39Counts[name] = count;
                var offsetField = type.GetField(name + "Offset");
                offsetField.SetValue(header, Convert.ChangeType(offset, offsetField.FieldType));
                var sizeField = type.GetField(name + "Size");
                sizeField.SetValue(header, size);
            }
        }

        private T[] ReadTableV39<T>(uint offset, int size, string table, Func<T> read)
        {
            var count = v39Counts[table];
            if (count == 0)
            {
                return new T[0];
            }
            var result = new T[count];
            Position = offset;
            for (var i = 0; i < count; i++)
            {
                var start = Position;
                result[i] = read();
                if (i == 0 && (long)(Position - start) * count != size)
                {
                    throw new NotSupportedException($"ERROR: Unsupported v39 layout of '{table}': table has {size / count} bytes per item but {Position - start} were expected.");
                }
            }
            return result;
        }

        // Tables whose layout is the same as in older versions.
        private T[] ReadFixedTableV39<T>(uint offset, int size, string table) where T : new()
        {
            var count = v39Counts[table];
            if (count != 0 && SizeOf(typeof(T)) * count != size)
            {
                throw new NotSupportedException($"ERROR: Unsupported v39 layout of '{table}': table has {size / count} bytes per item but {SizeOf(typeof(T))} were expected.");
            }
            return ReadClassArray<T>(offset, count);
        }

        private int ReadIndexV39(int size)
        {
            switch (size)
            {
                case 1:
                    var b = ReadByte();
                    return b == byte.MaxValue ? -1 : b;
                case 2:
                    var s = ReadUInt16();
                    return s == ushort.MaxValue ? -1 : s;
                default:
                    return ReadInt32();
            }
        }

        // Flat index arrays use the width stored in the file.
        private int[] ReadIndexArrayV39(uint offset, int size, string table)
        {
            var count = v39Counts[table];
            var result = new int[count];
            if (count == 0)
            {
                return result;
            }
            var elementSize = size / count;
            if (elementSize != 1 && elementSize != 2 && elementSize != 4)
            {
                throw new NotSupportedException($"ERROR: Unsupported v39 layout of '{table}': {elementSize} bytes per item.");
            }
            Position = offset;
            for (var i = 0; i < count; i++)
            {
                result[i] = ReadIndexV39(elementSize);
            }
            return result;
        }

        private Il2CppImageDefinition ReadImageDefinitionV39()
        {
            var imageDef = new Il2CppImageDefinition();
            imageDef.nameIndex = ReadUInt32();
            imageDef.assemblyIndex = ReadInt32();
            imageDef.typeStart = ReadIndexV39(typeDefIndexSize);
            imageDef.typeCount = ReadUInt32();
            imageDef.exportedTypeStart = ReadIndexV39(typeDefIndexSize);
            imageDef.exportedTypeCount = ReadUInt32();
            imageDef.entryPointIndex = ReadInt32();
            imageDef.token = ReadUInt32();
            imageDef.customAttributeStart = ReadInt32();
            imageDef.customAttributeCount = ReadUInt32();
            return imageDef;
        }

        private Il2CppAssemblyDefinition ReadAssemblyDefinitionV39()
        {
            var assemblyDef = new Il2CppAssemblyDefinition();
            assemblyDef.imageIndex = ReadInt32();
            assemblyDef.token = ReadUInt32();
            ReadInt32(); // unknown field added in v39
            assemblyDef.referencedAssemblyStart = ReadInt32();
            assemblyDef.referencedAssemblyCount = ReadInt32();
            assemblyDef.aname = ReadClass<Il2CppAssemblyNameDefinition>();
            return assemblyDef;
        }

        private Il2CppTypeDefinition ReadTypeDefinitionV39()
        {
            var typeDef = new Il2CppTypeDefinition();
            typeDef.nameIndex = ReadUInt32();
            typeDef.namespaceIndex = ReadUInt32();
            typeDef.byvalTypeIndex = ReadIndexV39(typeIndexSize);
            typeDef.declaringTypeIndex = ReadIndexV39(typeIndexSize);
            typeDef.parentIndex = ReadIndexV39(typeIndexSize);
            typeDef.elementTypeIndex = -1; // removed, see RestoreEnumElementTypesV39
            typeDef.genericContainerIndex = ReadIndexV39(genericContainerIndexSize);
            typeDef.flags = ReadUInt32();
            typeDef.fieldStart = ReadInt32();
            typeDef.methodStart = ReadInt32();
            typeDef.eventStart = ReadInt32();
            typeDef.propertyStart = ReadInt32();
            typeDef.nestedTypesStart = ReadInt32();
            typeDef.interfacesStart = ReadInt32();
            typeDef.vtableStart = ReadInt32();
            typeDef.interfaceOffsetsStart = ReadInt32();
            typeDef.method_count = ReadUInt16();
            typeDef.property_count = ReadUInt16();
            typeDef.field_count = ReadUInt16();
            typeDef.event_count = ReadUInt16();
            typeDef.nested_type_count = ReadUInt16();
            typeDef.vtable_count = ReadUInt16();
            typeDef.interfaces_count = ReadUInt16();
            typeDef.interface_offsets_count = ReadUInt16();
            typeDef.bitfield = ReadUInt32();
            typeDef.token = ReadUInt32();
            return typeDef;
        }

        private Il2CppMethodDefinition ReadMethodDefinitionV39()
        {
            var methodDef = new Il2CppMethodDefinition();
            methodDef.nameIndex = ReadUInt32();
            methodDef.declaringType = ReadIndexV39(typeDefIndexSize);
            methodDef.returnType = ReadIndexV39(typeIndexSize);
            methodDef.returnParameterToken = ReadInt32();
            methodDef.parameterStart = ReadIndexV39(parameterIndexSize);
            methodDef.genericContainerIndex = ReadIndexV39(genericContainerIndexSize);
            methodDef.token = ReadUInt32();
            methodDef.flags = ReadUInt16();
            methodDef.iflags = ReadUInt16();
            methodDef.slot = ReadUInt16();
            methodDef.parameterCount = ReadUInt16();
            return methodDef;
        }

        private Il2CppGenericParameter ReadGenericParameterV39()
        {
            var genericParameter = new Il2CppGenericParameter();
            genericParameter.ownerIndex = ReadIndexV39(genericContainerIndexSize);
            genericParameter.nameIndex = ReadUInt32();
            genericParameter.constraintsStart = ReadInt16();
            genericParameter.constraintsCount = ReadInt16();
            genericParameter.num = ReadUInt16();
            genericParameter.flags = ReadUInt16();
            return genericParameter;
        }

        // The table holds only data offsets and ends with a sentinel entry.
        private Il2CppStringLiteral[] ReadStringLiteralsV39()
        {
            var count = v39Counts["stringLiteral"];
            if (count == 0)
            {
                return new Il2CppStringLiteral[0];
            }
            Position = header.stringLiteralOffset;
            var offsets = new int[count];
            for (var i = 0; i < count; i++)
            {
                offsets[i] = ReadInt32();
            }
            var result = new Il2CppStringLiteral[count - 1];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = new Il2CppStringLiteral
                {
                    dataIndex = offsets[i],
                    length = (uint)(offsets[i + 1] - offsets[i])
                };
            }
            return result;
        }

        // For enums the parent slot does not hold System.Enum, so restore it.
        // The underlying type of an enum is the type of its value__ field.
        private void RestoreEnumElementTypesV39()
        {
            var systemEnum = typeDefs.FirstOrDefault(x => GetStringFromIndex(x.nameIndex) == "Enum" && GetStringFromIndex(x.namespaceIndex) == "System");
            foreach (var typeDef in typeDefs)
            {
                if (!typeDef.IsEnum)
                {
                    continue;
                }
                if (systemEnum != null)
                {
                    typeDef.parentIndex = systemEnum.byvalTypeIndex;
                }
                for (var i = 0; i < typeDef.field_count; i++)
                {
                    var fieldDef = fieldDefs[typeDef.fieldStart + i];
                    if (GetStringFromIndex(fieldDef.nameIndex) == "value__")
                    {
                        typeDef.elementTypeIndex = fieldDef.typeIndex;
                        break;
                    }
                }
            }
        }
    }
}
