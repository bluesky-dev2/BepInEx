﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using UnhollowerBaseLib;
using UnhollowerBaseLib.Runtime;
using UnhollowerBaseLib.Runtime.VersionSpecific.MethodInfo;
using ValueType = Il2CppSystem.ValueType;
using Void = Il2CppSystem.Void;

namespace BepInEx.IL2CPP.Hook
{
    public unsafe class IL2CPPDetourMethodPatcher : MethodPatcher
    {
        private static readonly MethodInfo IL2CPPToManagedStringMethodInfo
            = AccessTools.Method(typeof(UnhollowerBaseLib.IL2CPP),
                                 nameof(UnhollowerBaseLib.IL2CPP.Il2CppStringToManaged));

        private static readonly MethodInfo ManagedToIL2CPPStringMethodInfo
            = AccessTools.Method(typeof(UnhollowerBaseLib.IL2CPP),
                                 nameof(UnhollowerBaseLib.IL2CPP.ManagedStringToIl2Cpp));

        private static readonly MethodInfo ObjectBaseToPtrMethodInfo
            = AccessTools.Method(typeof(UnhollowerBaseLib.IL2CPP),
                                 nameof(UnhollowerBaseLib.IL2CPP.Il2CppObjectBaseToPtr));

        private static readonly MethodInfo ReportExceptionMethodInfo
            = AccessTools.Method(typeof(IL2CPPDetourMethodPatcher), nameof(ReportException));


        private static readonly ManualLogSource DetourLogger = Logger.CreateLogSource("Detour");

        // Map each value type to correctly sized store opcode to prevent memory overwrite
        // Special case: bool is byte in Il2Cpp
        private static readonly Dictionary<Type, OpCode> StIndOpcodes = new()
        {
            [typeof(byte)] = OpCodes.Stind_I1,
            [typeof(sbyte)] = OpCodes.Stind_I1,
            [typeof(bool)] = OpCodes.Stind_I1,
            [typeof(short)] = OpCodes.Stind_I2,
            [typeof(ushort)] = OpCodes.Stind_I2,
            [typeof(int)] = OpCodes.Stind_I4,
            [typeof(uint)] = OpCodes.Stind_I4,
            [typeof(long)] = OpCodes.Stind_I8,
            [typeof(ulong)] = OpCodes.Stind_I8,
            [typeof(float)] = OpCodes.Stind_R4,
            [typeof(double)] = OpCodes.Stind_R8
        };

        private static AssemblyBuilder fixedStructAssembly;
        private static ModuleBuilder fixedStructModuleBuilder;
        private static readonly Dictionary<int, Type> FixedStructCache = new();

        private bool isValid;
        private INativeMethodStruct modifiedNativeMethodInfo;

        private FastNativeDetour nativeDetour;

        private INativeMethodStruct originalNativeMethodInfo;

        /// <summary>
        ///     Constructs a new instance of <see cref="NativeDetour" /> method patcher.
        /// </summary>
        /// <param name="original"></param>
        public IL2CPPDetourMethodPatcher(MethodBase original) : base(original)
        {
            Init();
        }

        private void Init()
        {
            try
            {
                var methodField = UnhollowerUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(Original);

                if (methodField == null)
                {
                    var fieldInfoField =
                        UnhollowerUtils.GetIl2CppFieldInfoPointerFieldForGeneratedFieldAccessor(Original);

                    if (fieldInfoField != null)
                        throw new
                            Exception($"Method {Original.FullDescription()} is a field accessor, it can't be patched.");

                    // Generated method is probably unstripped, it can be safely handed to IL handler
                    return;
                }

                // Get the native MethodInfo struct for the target method
                originalNativeMethodInfo =
                    UnityVersionHandler.Wrap((Il2CppMethodInfo*) (IntPtr) methodField.GetValue(null));

                // Create a trampoline from the original target method
                var trampolinePtr =
                    DetourGenerator.CreateTrampolineFromFunction(originalNativeMethodInfo.MethodPointer, out _, out _);

                // Create a modified native MethodInfo struct to point towards the trampoline
                modifiedNativeMethodInfo =
                    UnityVersionHandler
                        .NewMethod(); //(Il2CppMethodInfo*) Marshal.AllocHGlobal(Marshal.SizeOf<Il2CppMethodInfo>());
                Buffer.MemoryCopy(originalNativeMethodInfo.Pointer.ToPointer(),
                                  modifiedNativeMethodInfo.Pointer.ToPointer(), modifiedNativeMethodInfo.StructSize,
                                  originalNativeMethodInfo.StructSize);
                modifiedNativeMethodInfo.MethodPointer = trampolinePtr;
                isValid = true;
            }
            catch (Exception e)
            {
                DetourLogger
                    .LogWarning($"Failed to init IL2CPP patch backend for {Original.FullDescription()}, using normal patch handlers: {e.Message}");
            }
        }

        /// <inheritdoc />
        public override DynamicMethodDefinition PrepareOriginal() => null;

        /// <inheritdoc />
        public override MethodBase DetourTo(MethodBase replacement)
        {
            // Unpatch an existing detour if it exists
            nativeDetour?.Dispose();

            // Generate a new DMD of the modified unhollowed method, and apply harmony patches to it
            var copiedDmd = CopyOriginal();

            HarmonyManipulator.Manipulate(copiedDmd.OriginalMethod, copiedDmd.OriginalMethod.GetPatchInfo(),
                                          new ILContext(copiedDmd.Definition));

            // Generate the MethodInfo instances
            var managedHookedMethod = copiedDmd.Generate();
            var unmanagedTrampolineMethod = GenerateNativeToManagedTrampoline(managedHookedMethod).Generate();

            // Apply a detour from the unmanaged implementation to the patched harmony method
            var unmanagedDelegateType = DelegateTypeFactory.instance.CreateDelegateType(unmanagedTrampolineMethod,
                CallingConvention.Cdecl);

            var detourPtr =
                Marshal.GetFunctionPointerForDelegate(unmanagedTrampolineMethod.CreateDelegate(unmanagedDelegateType));
            nativeDetour = new FastNativeDetour(originalNativeMethodInfo.MethodPointer, detourPtr);
            nativeDetour.Apply();

            // TODO: Add an ILHook for the original unhollowed method to go directly to managedHookedMethod
            // Right now it goes through three times as much interop conversion as it needs to, when being called from managed side
            return managedHookedMethod;
        }

        /// <inheritdoc />
        public override DynamicMethodDefinition CopyOriginal()
        {
            var dmd = new DynamicMethodDefinition(Original);
            dmd.Definition.Name = "UnhollowedWrapper_" + dmd.Definition.Name;
            var cursor = new ILCursor(new ILContext(dmd.Definition));


            // Remove il2cpp_object_get_virtual_method
            if (cursor.TryGotoNext(x => x.MatchLdarg(0),
                                   x => x.MatchCall(typeof(UnhollowerBaseLib.IL2CPP),
                                                    nameof(UnhollowerBaseLib.IL2CPP.Il2CppObjectBaseToPtr)),
                                   x => x.MatchLdsfld(out _),
                                   x => x.MatchCall(typeof(UnhollowerBaseLib.IL2CPP),
                                                    nameof(UnhollowerBaseLib.IL2CPP.il2cpp_object_get_virtual_method))))
                cursor.RemoveRange(4);
            else
                cursor.Goto(0)
                      .GotoNext(x =>
                                    x.MatchLdsfld(UnhollowerUtils
                                                      .GetIl2CppMethodInfoPointerFieldForGeneratedMethod(Original)))
                      .Remove();

            // Replace original IL2CPPMethodInfo pointer with a modified one that points to the trampoline
            cursor
                .Emit(Mono.Cecil.Cil.OpCodes.Ldc_I8, modifiedNativeMethodInfo.Pointer.ToInt64())
                .Emit(Mono.Cecil.Cil.OpCodes.Conv_I);

            return dmd;
        }

        /// <summary>
        ///     A handler for <see cref="PatchManager.ResolvePatcher" /> that checks if a method doesn't have a body
        ///     (e.g. it's icall or marked with <see cref="DynDllImportAttribute" />) and thus can be patched with
        ///     <see cref="NativeDetour" />.
        /// </summary>
        /// <param name="sender">Not used</param>
        /// <param name="args">Patch resolver arguments</param>
        public static void TryResolve(object sender, PatchManager.PatcherResolverEventArgs args)
        {
            if (args.Original.DeclaringType?.IsSubclassOf(typeof(Il2CppObjectBase)) == true)
            {
                var backend = new IL2CPPDetourMethodPatcher(args.Original);
                if (backend.isValid)
                    args.MethodPatcher = backend;
            }
        }

        private DynamicMethodDefinition GenerateNativeToManagedTrampoline(MethodInfo targetManagedMethodInfo)
        {
            // managedParams are the unhollower types used on the managed side
            // unmanagedParams are IntPtr references that are used by IL2CPP compiled assembly
            var paramStartIndex = Original.IsStatic ? 0 : 1;
            var managedParams = Original.GetParameters().Select(x => x.ParameterType).ToArray();
            var unmanagedParams =
                new Type[managedParams.Length + paramStartIndex +
                         1]; // +1 for thisptr if needed, +1 for methodInfo at the end

            if (!Original.IsStatic)
                unmanagedParams[0] = typeof(IntPtr);
            unmanagedParams[^1] = typeof(Il2CppMethodInfo*);
            Array.Copy(managedParams.Select(ConvertManagedTypeToIL2CPPType).ToArray(), 0,
                       unmanagedParams, paramStartIndex, managedParams.Length);

            var managedReturnType = AccessTools.GetReturnedType(Original);
            var unmanagedReturnType = ConvertManagedTypeToIL2CPPType(managedReturnType);

            var dmd = new DynamicMethodDefinition("(il2cpp -> managed) " + Original.Name,
                                                  unmanagedReturnType,
                                                  unmanagedParams
                                                 );

            var il = dmd.GetILGenerator();
            il.BeginExceptionBlock();

            // Declare a list of variables to dereference back to the original pointers.
            // This is required due to the needed unhollower type conversions, so we can't directly pass some addresses as byref types
            var indirectVariables = new LocalBuilder[managedParams.Length];

            if (!Original.IsStatic)
                EmitConvertArgumentToManaged(il, 0, Original.DeclaringType, out _);
            for (var i = 0; i < managedParams.Length; ++i)
                EmitConvertArgumentToManaged(il, i + paramStartIndex, managedParams[i], out indirectVariables[i]);

            // Run the managed method
            il.Emit(OpCodes.Call, targetManagedMethodInfo);

            // Store the managed return type temporarily (if there was one)
            LocalBuilder managedReturnVariable = null;
            if (managedReturnType != typeof(void))
            {
                managedReturnVariable = il.DeclareLocal(managedReturnType);
                il.Emit(OpCodes.Stloc, managedReturnVariable);
            }

            // Convert any managed byref values into their relevant IL2CPP types, and then store the values into their relevant dereferenced pointers
            for (var i = 0; i < managedParams.Length; ++i)
            {
                if (indirectVariables[i] == null)
                    continue;

                il.Emit(OpCodes.Ldarg_S, i + paramStartIndex);
                il.Emit(OpCodes.Ldloc, indirectVariables[i]);
                var directType = managedParams[i].GetElementType();
                EmitConvertManagedTypeToIL2CPP(il, directType);
                il.Emit(StIndOpcodes.TryGetValue(directType, out var stindOpCodde) ? stindOpCodde : OpCodes.Stind_I);
            }

            // Handle any lingering exceptions
            il.BeginCatchBlock(typeof(Exception));
            il.Emit(OpCodes.Call, ReportExceptionMethodInfo);
            il.EndExceptionBlock();

            // Convert the return value back to an IL2CPP friendly type (if there was a return value), and then return
            if (managedReturnVariable != null)
            {
                il.Emit(OpCodes.Ldloc, managedReturnVariable);
                EmitConvertManagedTypeToIL2CPP(il, managedReturnType);
            }

            il.Emit(OpCodes.Ret);

            return dmd;
        }

        private static void ReportException(Exception ex) => DetourLogger.LogError(ex.ToString());

        private static Type ConvertManagedTypeToIL2CPPType(Type managedType)
        {
            if (managedType.IsByRef)
            {
                var directType = managedType.GetElementType();

                // bool is byte in Il2Cpp, but int in CLR => force size to be correct
                if (directType == typeof(bool))
                    return typeof(byte).MakeByRefType();
                if (directType == typeof(string) || directType.IsSubclassOf(typeof(Il2CppObjectBase)))
                    return typeof(IntPtr*);
            }
            else if (managedType.IsSubclassOf(typeof(ValueType))
            ) // Struct that's passed on the stack => handle as general struct
            {
                uint align = 0;
                var fixedSize =
                    UnhollowerBaseLib.IL2CPP.il2cpp_class_value_size(Il2CppTypeToClassPointer(managedType), ref align);
                return GetFixedSizeStructType(fixedSize);
            }
            else if (managedType == typeof(string) || managedType.IsSubclassOf(typeof(Il2CppObjectBase))
            ) // General reference type
            {
                return typeof(IntPtr);
            }

            return managedType;
        }

        private static void EmitConvertManagedTypeToIL2CPP(ILGenerator il, Type returnType)
        {
            if (returnType == typeof(string))
                il.Emit(OpCodes.Call, ManagedToIL2CPPStringMethodInfo);
            else if (!returnType.IsValueType && returnType.IsSubclassOf(typeof(Il2CppObjectBase)))
                il.Emit(OpCodes.Call, ObjectBaseToPtrMethodInfo);
        }

        private static IntPtr Il2CppTypeToClassPointer(Type type)
        {
            if (type == typeof(void))
                return Il2CppClassPointerStore<Void>.NativeClassPtr;
            return (IntPtr) typeof(Il2CppClassPointerStore<>).MakeGenericType(type).GetField("NativeClassPtr")
                                                             .GetValue(null);
        }

        private static void EmitConvertArgumentToManaged(ILGenerator il,
                                                         int argIndex,
                                                         Type managedParamType,
                                                         out LocalBuilder variable)
        {
            variable = null;

            // Box struct into object first before conversion
            // This will likely incur struct copying down the line, but it shouldn't be a massive loss
            if (managedParamType.IsSubclassOf(typeof(ValueType)))
            {
                il.Emit(OpCodes.Ldc_I8, Il2CppTypeToClassPointer(managedParamType).ToInt64());
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Ldarga_S, argIndex);
                il.Emit(OpCodes.Call,
                        AccessTools.Method(typeof(UnhollowerBaseLib.IL2CPP),
                                           nameof(UnhollowerBaseLib.IL2CPP.il2cpp_value_box)));
            }
            else
            {
                il.Emit(OpCodes.Ldarg_S, argIndex);
            }

            if (managedParamType.IsValueType) // don't need to convert blittable types
                return;

            void EmitCreateIl2CppObject(Type originalType)
            {
                var endLabel = il.DefineLabel();
                var notNullLabel = il.DefineLabel();

                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Brtrue_S, notNullLabel);

                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Br_S, endLabel);

                il.MarkLabel(notNullLabel);
                il.Emit(OpCodes.Newobj, AccessTools.DeclaredConstructor(originalType, new[] { typeof(IntPtr) }));

                il.MarkLabel(endLabel);
            }

            void HandleTypeConversion(Type originalType)
            {
                if (originalType == typeof(string))
                    il.Emit(OpCodes.Call, IL2CPPToManagedStringMethodInfo);
                else if (originalType.IsSubclassOf(typeof(Il2CppObjectBase)))
                    EmitCreateIl2CppObject(originalType);
            }

            if (managedParamType.IsByRef)
            {
                // TODO: directType being ValueType is not handled yet (but it's not that common in games). Implement when needed.
                var directType = managedParamType.GetElementType();

                variable = il.DeclareLocal(directType);

                il.Emit(OpCodes.Ldind_I);

                HandleTypeConversion(directType);

                il.Emit(OpCodes.Stloc, variable);
                il.Emit(OpCodes.Ldloca, variable);
            }
            else
            {
                HandleTypeConversion(managedParamType);
            }
        }

        private static Type GetFixedSizeStructType(int size)
        {
            if (FixedStructCache.TryGetValue(size, out var result))
                return result;

            fixedStructAssembly ??=
                AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FixedSizeStructAssembly"),
                                                      AssemblyBuilderAccess.RunAndCollect);
            fixedStructModuleBuilder ??= fixedStructAssembly.DefineDynamicModule("FixedSizeStructAssembly");

            var tb = fixedStructModuleBuilder.DefineType($"IL2CPPDetour_FixedSizeStruct_{size}b",
                                                         TypeAttributes.SequentialLayout, typeof(System.ValueType));
            var fb = tb.DefineField("buffer", typeof(IntPtr), FieldAttributes.Public);
            fb.SetCustomAttribute(new
                                      CustomAttributeBuilder(AccessTools.Constructor(typeof(MarshalAsAttribute), new[] { typeof(UnmanagedType) }),
                                                             new object[] { UnmanagedType.ByValArray },
                                                             new[]
                                                             {
                                                                 AccessTools.Field(typeof(MarshalAsAttribute),
                                                                     nameof(MarshalAsAttribute.SizeConst)),
                                                                 AccessTools.Field(typeof(MarshalAsAttribute),
                                                                     nameof(MarshalAsAttribute.ArraySubType))
                                                             }, new object[] { size, UnmanagedType.U1 }));

            var type = tb.CreateType();
            return FixedStructCache[size] = type;
        }
    }
}
