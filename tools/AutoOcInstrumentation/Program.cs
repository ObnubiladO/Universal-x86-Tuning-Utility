using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length is not (3 or 4) || (args.Length == 4 && args[3] != "--cpu-policy-v1"))
    throw new ArgumentException("Usage: AutoOcInstrumentation <original AutoOC.dll> <diagnostic UXTU.dll> <output AutoOC.dll> [--cpu-policy-v1]");
bool cpuPolicyEnabled = args.Length == 4;
string originalPath = Path.GetFullPath(args[0]), hostPath = Path.GetFullPath(args[1]), outputPath = Path.GetFullPath(args[2]);
if (originalPath.Equals(outputPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must differ from original DLL.");
string originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(originalPath)));
if (originalHash != "E6B9CA8BC140D92A20B6D6F69F21A351813B26A82EB0CC9CF1379CA408CCBB8F")
    throw new InvalidOperationException("Only the audited AutoOC.dll from UXTU 26.3.1 is supported; review a different build before instrumenting it.");
using var assembly = AssemblyDefinition.ReadAssembly(originalPath);
using var host = AssemblyDefinition.ReadAssembly(hostPath);
var module = assembly.MainModule;
var monitor = module.Types.Single(t => t.FullName == "AutoOC.Monitors.InstabilityMonitor");
var trace = host.MainModule.Types.Single(t => t.FullName == "Universal_x86_Tuning_Utility.Scripts.Misc.AutoOcDiagnostics");
var setContext = module.ImportReference(trace.Methods.Single(m => m.Name == "SetSignalContext"));
var onSignal = module.ImportReference(trace.Methods.Single(m => m.Name == "OnSignal"));
if (monitor.Methods.Any(m => m.HasBody && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.DeclaringType.FullName == trace.FullName)))
    throw new InvalidOperationException("DLL is already instrumented.");
if (assembly.Name.HasPublicKey) throw new InvalidOperationException("Refusing to modify a signed assembly.");
var allOriginalInstructions = AllTypes(module.Types).SelectMany(t => t.Methods).Where(m => m.HasBody)
    .ToDictionary(m => m, m => m.Body.Instructions.ToArray());
var originalInstructions = allOriginalInstructions.Where(pair => pair.Key.DeclaringType == monitor)
    .ToDictionary(pair => pair.Key, pair => pair.Value);
var originalOpcodes = allOriginalInstructions.Values.SelectMany(x => x).ToDictionary(i => i, i => (i.OpCode, i.Operand));
var sites = new List<object>();
foreach (var (method, instructions) in originalInstructions)
{
    var il = method.Body.GetILProcessor();
    int ordinal = 0;
    foreach (var instruction in instructions)
    {
        if (instruction.OpCode != OpCodes.Call || instruction.Operand is not MethodReference target ||
            target.DeclaringType.FullName != monitor.FullName || target.Name is not ("SignalCpuInstability" or "SignalGpuInstability")) continue;
        string source = $"{method.Name}:{++ordinal}@IL_{instruction.Offset:x4}";
        var injected = new List<Instruction> { il.Create(OpCodes.Dup), il.Create(OpCodes.Ldstr, source) };
        bool hasEventArgs = method.Parameters.Count == 2 && method.Parameters[1].ParameterType.FullName.EndsWith("EventArgs", StringComparison.Ordinal);
        injected.Add(hasEventArgs ? il.Create(OpCodes.Ldarg, method.Parameters[1]) : il.Create(OpCodes.Ldnull));
        injected.Add(il.Create(OpCodes.Call, setContext));
        RedirectTargets(method, instruction, injected[0]);
        foreach (var added in injected) il.InsertBefore(instruction, added);
        sites.Add(new { source, signal = target.Name, eventArgumentsCaptured = hasEventArgs });
    }
}
foreach (string signalName in new[] { "SignalCpuInstability", "SignalGpuInstability" })
{
    var method = monitor.Methods.Single(m => m.Name == signalName);
    var il = method.Body.GetILProcessor();
    var first = method.Body.Instructions[0];
    il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
    il.InsertBefore(first, il.Create(OpCodes.Ldstr, signalName.Contains("Cpu") ? "cpu" : "igpu"));
    il.InsertBefore(first, il.Create(OpCodes.Call, onSignal));
    method.ImplAttributes |= MethodImplAttributes.NoInlining;
}
if (sites.Count == 0) throw new InvalidOperationException("No direct signal call sites found.");
// Existing instructions and semantic operands must remain intact. Only branch
// destinations that now include a diagnostic prelude may have been redirected.
foreach (var (method, instructions) in allOriginalInstructions)
    foreach (var instruction in instructions)
    {
        if (!method.Body.Instructions.Contains(instruction) || instruction.OpCode != originalOpcodes[instruction].OpCode)
            throw new InvalidOperationException("An original instruction was removed or changed.");
        if (instruction.OpCode.OperandType is not (OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget or OperandType.InlineSwitch) &&
            !Equals(instruction.Operand, originalOpcodes[instruction].Operand))
            throw new InvalidOperationException("An original semantic operand changed.");
    }

var policyPatches = new List<object>();
var intentionalOperands = new Dictionary<Instruction, object>();
if (cpuPolicyEnabled)
{
    // Match the audited structure exactly. Do not turn this into a broad signal
    // replacement: watchdogs, exceptions, WHEA and every other caller must retain
    // their original flag-setting behavior and the host guard must remain intact.
    var policyType = host.MainModule.Types.Single(t => t.FullName == "Universal_x86_Tuning_Utility.Scripts.Misc.AutoOcCpuPolicy");
    var policyHook = policyType.Methods.Single(m => m.Name == "ObserveWorkloadSignal" && m.IsPublic && m.IsStatic &&
        m.ReturnType.FullName == "System.Void" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Object");
    var workloadSites = allOriginalInstructions.SelectMany(pair => pair.Value.Select(instruction => (Method: pair.Key, Instruction: instruction)))
        .Where(site => site.Method.DeclaringType == monitor && site.Method.Name == "MonitorPerformanceCounters" &&
            site.Instruction.OpCode == OpCodes.Call && site.Instruction.Operand is MethodReference called &&
            called.DeclaringType.FullName == monitor.FullName && called.Name == "SignalCpuInstability").ToArray();
    const string oldWheaQuery = "*[System[Provider[@Name='WHEA-Logger']]]";
    const string newWheaQuery = "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] or Provider[@Name='WHEA-Logger']]]";
    const string oldCpuState = "uv_state_cpu.json", newCpuState = "uv_state_cpu_verified_v1.json";
    var wheaSites = allOriginalInstructions.SelectMany(pair => pair.Value.Select(instruction => (Method: pair.Key, Instruction: instruction)))
        .Where(site => site.Instruction.OpCode == OpCodes.Ldstr && Equals(site.Instruction.Operand, oldWheaQuery)).ToArray();
    var stateSites = allOriginalInstructions.SelectMany(pair => pair.Value.Select(instruction => (Method: pair.Key, Instruction: instruction)))
        .Where(site => site.Instruction.OpCode == OpCodes.Ldstr && Equals(site.Instruction.Operand, oldCpuState)).ToArray();
    if (workloadSites.Length != 1 || workloadSites[0].Instruction.Offset != 0x0708 ||
        wheaSites.Length != 1 || wheaSites[0].Method.DeclaringType != monitor || !wheaSites[0].Method.IsConstructor ||
        stateSites.Length != 1 || stateSites[0].Method.DeclaringType.FullName != "AutoOC.Controllers.AdaptiveUndervoltController/StateStore" || !stateSites[0].Method.IsConstructor)
        throw new InvalidOperationException("CPU policy requires exactly the three audited call/query/state sites; refusing an unexpected DLL structure.");

    ReplacePolicyOperand(workloadSites[0].Method, workloadSites[0].Instruction, module.ImportReference(policyHook),
        "workload_signal_observed_without_cpu_instability_flag", intentionalOperands, policyPatches);
    ReplacePolicyOperand(wheaSites[0].Method, wheaSites[0].Instruction, newWheaQuery,
        "watch_both_registered_and_legacy_whea_provider_names", intentionalOperands, policyPatches);
    ReplacePolicyOperand(stateSites[0].Method, stateSites[0].Instruction, newCpuState,
        "isolate_cpu_learned_state_and_its_derived_backup", intentionalOperands, policyPatches);

    // Use the target's existing framework scope, not this tool's runtime version.
    // The host requires this marker before starting its policy-specific CPU path.
    var metadataType = new TypeReference("System.Reflection", "AssemblyMetadataAttribute", module,
        assembly.CustomAttributes.Single(a => a.AttributeType.FullName == "System.Reflection.AssemblyProductAttribute").AttributeType.Scope);
    var metadataConstructor = new MethodReference(".ctor", module.TypeSystem.Void, metadataType) { HasThis = true };
    metadataConstructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
    metadataConstructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
    var policyMarker = new CustomAttribute(metadataConstructor);
    policyMarker.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, "AutoOcCpuPolicy"));
    policyMarker.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, "cpu-policy-v1"));
    assembly.CustomAttributes.Add(policyMarker);
}
// Recheck the entire module after policy changes, allowing only the three exact
// operands above. No controller rules, iGPU paths, or host-guard calls are changed.
foreach (var (method, instructions) in allOriginalInstructions)
    foreach (var instruction in instructions)
    {
        if (!method.Body.Instructions.Contains(instruction) || instruction.OpCode != originalOpcodes[instruction].OpCode)
            throw new InvalidOperationException("An original instruction was removed or changed.");
        object expected = intentionalOperands.TryGetValue(instruction, out var replacement) ? replacement : originalOpcodes[instruction].Operand;
        if (instruction.OpCode.OperandType is not (OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget or OperandType.InlineSwitch) &&
            !Equals(instruction.Operand, expected))
            throw new InvalidOperationException("An original operand changed outside the explicit CPU policy patches.");
    }
// Added preludes can push branch destinations beyond the short-branch range.
foreach (var method in monitor.Methods.Where(m => m.HasBody))
    foreach (var instruction in method.Body.Instructions)
        if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget)
            instruction.OpCode = instruction.OpCode.Code switch
            {
                Code.Br_S => OpCodes.Br, Code.Brfalse_S => OpCodes.Brfalse, Code.Brtrue_S => OpCodes.Brtrue,
                Code.Beq_S => OpCodes.Beq, Code.Bge_S => OpCodes.Bge, Code.Bge_Un_S => OpCodes.Bge_Un,
                Code.Bgt_S => OpCodes.Bgt, Code.Bgt_Un_S => OpCodes.Bgt_Un, Code.Ble_S => OpCodes.Ble,
                Code.Ble_Un_S => OpCodes.Ble_Un, Code.Blt_S => OpCodes.Blt, Code.Blt_Un_S => OpCodes.Blt_Un,
                Code.Bne_Un_S => OpCodes.Bne_Un, Code.Leave_S => OpCodes.Leave,
                _ => throw new InvalidOperationException("Unknown short branch.")
            };
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
assembly.Write(outputPath);
var manifest = new {
    originalSha256 = originalHash,
    instrumentedSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(outputPath))),
    directSignalCallSites = sites,
    centralSignalsInstrumented = 2,
    originalInstructionsPreserved = !cpuPolicyEnabled,
    tuningRulesChanged = cpuPolicyEnabled,
    cpuPolicyEnabled,
    cpuPolicyVersion = cpuPolicyEnabled ? "cpu-policy-v1" : null,
    policyAssemblyMetadata = cpuPolicyEnabled ? new { key = "AutoOcCpuPolicy", value = "cpu-policy-v1" } : null,
    policyPatches
};
File.WriteAllText(outputPath + ".instrumentation.json", JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Instrumented {sites.Count} direct signal sites and both signal methods. " +
    (cpuPolicyEnabled ? "CPU policy v1 enabled: three audited operand changes; other original instructions preserved." : "Original instructions preserved."));

static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
{
    foreach (var type in types)
    {
        yield return type;
        foreach (var nested in AllTypes(type.NestedTypes)) yield return nested;
    }
}

static void ReplacePolicyOperand(MethodDefinition method, Instruction instruction, object replacement, string purpose,
    Dictionary<Instruction, object> intentionalOperands, List<object> patches)
{
    patches.Add(new { purpose, method = method.FullName, originalIlOffset = $"0x{instruction.Offset:X4}",
        originalOperand = instruction.Operand.ToString(), replacementOperand = replacement.ToString() });
    instruction.Operand = replacement;
    intentionalOperands.Add(instruction, replacement);
}

static void RedirectTargets(MethodDefinition method, Instruction original, Instruction replacement)
{
    foreach (var instruction in method.Body.Instructions)
    {
        if (ReferenceEquals(instruction.Operand, original)) instruction.Operand = replacement;
        else if (instruction.Operand is Instruction[] targets)
            for (int i = 0; i < targets.Length; i++) if (ReferenceEquals(targets[i], original)) targets[i] = replacement;
    }
    foreach (var handler in method.Body.ExceptionHandlers)
    {
        if (handler.TryStart == original) handler.TryStart = replacement;
        if (handler.TryEnd == original) handler.TryEnd = replacement;
        if (handler.HandlerStart == original) handler.HandlerStart = replacement;
        if (handler.HandlerEnd == original) handler.HandlerEnd = replacement;
        if (handler.FilterStart == original) handler.FilterStart = replacement;
    }
}
