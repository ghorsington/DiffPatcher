﻿using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace NetPatch
{
    public class NetDiffer : DiffPatchBase
    {
        private static readonly DelegateComparer<Instruction> InstructionComparer =
            new DelegateComparer<Instruction>((i1, i2) => i1.OpCode == i2.OpCode);

        private readonly Dictionary<string, List<FieldDefinition>> fieldsToInclude =
            new Dictionary<string, List<FieldDefinition>>();

        private readonly Dictionary<string, List<MethodDefinition>> methodsToInclude =
            new Dictionary<string, List<MethodDefinition>>();

        private readonly Dictionary<string, List<TypeDefinition>> nestedTypesToInclude =
            new Dictionary<string, List<TypeDefinition>>();

        private readonly HashSet<TypeDefinition> typesToInclude = new HashSet<TypeDefinition>();

        private readonly ModuleDefinition from;
        private readonly ModuleDefinition to;

        public NetDiffer(ModuleDefinition from, ModuleDefinition to)
        {
            this.from = from;
            this.to = to;

            InitDiff(to.Types);
        }

        public AssemblyDefinition CreateDiffAssembly()
        {
            var diffDll = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition($"{from.Assembly.Name.Name}_diff", new Version(1, 0)),
                $"{from.Assembly.Name.Name}_diff", ModuleKind.Dll);

            var diff = diffDll.MainModule;

            // Reference original assembly to resolve stuff we don't import
            diff.AssemblyReferences.Add(to.Assembly.Name);

            RegisterTypes(diff, typesToInclude);
            GenerateTypeDefinitions(to, diff);
            CopyInstructions(to, diff);
            CopyAttributesAndProperties(to, diff);

            return diffDll;
        }

        public override void Dispose()
        {
            from?.Dispose();
            to?.Dispose();
        }

        protected override IEnumerable<TypeDefinition> GetChildrenToInclude(TypeDefinition type)
        {
            return nestedTypesToInclude.TryGetValue(type.FullName, out var result) ? result : null;
        }

        protected override IEnumerable<FieldDefinition> GetFieldsToInclude(TypeDefinition td)
        {
            return fieldsToInclude.TryGetValue(td.FullName, out var result) ? result : new List<FieldDefinition>();
        }

        protected override IEnumerable<MethodDefinition> GetMethodsToInclude(TypeDefinition td)
        {
            return methodsToInclude.TryGetValue(td.FullName, out var result) ? result : new List<MethodDefinition>();
        }

        private void InitDiff(IEnumerable<TypeDefinition> toTypes, TypeDefinition parent = null)
        {
            foreach (var toType in toTypes)
            {
                if (ExcludeTypes.Contains(toType.FullName) || ExcludeNamespaces.Contains(toType.Namespace))
                    continue;

                var fromType = from.GetType(toType.FullName);

                if (fromType == null)
                {
                    fieldsToInclude[toType.FullName] = toType.Fields.ToList();
                    methodsToInclude[toType.FullName] = toType.Methods.ToList();

                    if (parent == null)
                        typesToInclude.Add(toType);
                    else
                    {
                        if (!nestedTypesToInclude.TryGetValue(parent.FullName, out var list))
                            nestedTypesToInclude[parent.FullName] = list = new List<TypeDefinition>();
                        list.Add(toType);
                    }

                    InitDiff(toType.NestedTypes, toType);
                    continue;
                }

                foreach (var toField in toType.Fields)
                {
                    var fromField = fromType.Fields.FirstOrDefault(f => f.Name == toField.Name);

                    if (fromField == null || fromField.FieldType.FullName != toField.FieldType.FullName ||
                        fromField.Attributes != toField.Attributes)
                    {
                        if (!fieldsToInclude.TryGetValue(toType.FullName, out var list))
                            fieldsToInclude[toType.FullName] = list = new List<FieldDefinition>();
                        list.Add(toField);
                    }
                }

                foreach (var toMethod in toType.Methods)
                {
                    var fromMethod = fromType.Methods.FirstOrDefault(m => m.FullName == toMethod.FullName);

                    if (fromMethod == null || fromMethod.HasBody != toMethod.HasBody ||
                        fromMethod.Body?.Instructions.Count != toMethod.Body?.Instructions.Count)
                    {
                        if (!methodsToInclude.TryGetValue(toType.FullName, out var list))
                            methodsToInclude[toType.FullName] = list = new List<MethodDefinition>();
                        list.Add(toMethod);
                        continue;
                    }

                    if (toMethod.HasBody &&
                        !toMethod.Body.Instructions.SequenceEqual(fromMethod.Body.Instructions, InstructionComparer))
                    {
                        if (!methodsToInclude.TryGetValue(toType.FullName, out var list))
                            methodsToInclude[toType.FullName] = list = new List<MethodDefinition>();
                        list.Add(toMethod);
                    }
                }

                if (fieldsToInclude.ContainsKey(toType.FullName) || methodsToInclude.ContainsKey(toType.FullName) ||
                    nestedTypesToInclude.ContainsKey(toType.FullName))
                {
                    if (parent == null)
                        typesToInclude.Add(toType);
                    else
                    {
                        if (!nestedTypesToInclude.TryGetValue(toType.FullName, out var list))
                            nestedTypesToInclude[toType.FullName] = list = new List<TypeDefinition>();
                        list.Add(toType);
                    }
                }

                InitDiff(toType.NestedTypes, toType);
            }
        }

        private class DelegateComparer<T> : IEqualityComparer<T>
        {
            private readonly Func<T, T, bool> comparer;

            public DelegateComparer(Func<T, T, bool> comparer) { this.comparer = comparer; }

            public bool Equals(T x, T y) { return comparer(x, y); }

            public int GetHashCode(T obj) { return obj.GetHashCode(); }
        }
    }
}