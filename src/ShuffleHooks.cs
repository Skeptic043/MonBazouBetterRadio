using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace RadioEnhancements
{
    internal sealed class ShuffleHooks
    {
        internal readonly MethodInfo Tick, Preset, Hover, Next, Previous;
        private readonly MethodInfo randomSong, select;
        private static MethodInfo nativeRandom;
        private readonly PropertyInfo index, time, mode;
        private readonly FieldInfo library, radio, furnitureMode, presetIndex, presetRadio;
        private readonly PropertyInfo power;
        internal Func<bool> PreferredShuffle;
        private readonly WeakIdentityStateStore<ShuffleSequence> sequences = new WeakIdentityStateStore<ShuffleSequence>();
        internal ShuffleHooks()
        {
            Type manager = GameHooks.RequiredType("SOUND_Manager"), sound = GameHooks.RequiredType("SOUND_Radio"), furniture = GameHooks.RequiredType("Furniture_Radio");
            Type button = GameHooks.RequiredType("Furniture_Radio_ButtonPreset");
            Tick = GameHooks.Method(manager, "Tick", typeof(void), typeof(int));
            Preset = GameHooks.Method(furniture, "Button_Presets", typeof(void), typeof(int));
            Hover = GameHooks.Method(button, "Update_NameToAddAfter", typeof(string));
            Next = GameHooks.Method(manager, "Radio_SeekNext", typeof(void), sound);
            Previous = GameHooks.Method(manager, "Radio_SeekPrev", typeof(void), sound);
            select = GameHooks.Method(manager, "PlayStationOnThisRadio", typeof(void), sound);
            randomSong = GameHooks.Method(manager, "GetNewRandomSong", typeof(int), typeof(int), typeof(int));
            nativeRandom = randomSong;
            index = GameHooks.Property(sound, "GetCurrentCDSong", typeof(int));
            time = GameHooks.Property(sound, "GetCurrentCDTime", typeof(float));
            if (index.GetSetMethod() == null || time.GetSetMethod() == null) throw new MissingMethodException("CD state setters changed.");
            mode = GameHooks.Property(sound, "GetFMCD", typeof(float));
            power = GameHooks.Property(furniture, "Get_IsOn", typeof(bool));
            library = GameHooks.Field(manager, "CDPlayer", typeof(List<AudioClip>), false);
            radio = GameHooks.Field(furniture, "SOUND_Radio", sound, false);
            furnitureMode = GameHooks.Field(furniture, "FMCD", typeof(int), false);
            presetIndex = GameHooks.Field(button, "PresetIndex", typeof(int), false);
            presetRadio = GameHooks.Field(button, "Furniture_Radio", furniture, false);
        }
        private ShuffleSequence State(object sound)
        {
            ShuffleSequence state = sequences.GetOrAdd(sound, delegate { return new ShuffleSequence(Guid.NewGuid().GetHashCode()); });
            bool preferred = PreferredShuffle == null ? state.Enabled : PreferredShuffle();
            if (state.Enabled != preferred) { state.Reset(); state.Enabled = preferred; }
            return state;
        }
        internal void ResetLibrary() { sequences.Clear(); }
        internal bool Toggle(object furniture, int preset, out bool enabled)
        {
            enabled = false;
            if (preset != 6 || !CdOn(furniture)) return false;
            object sound = radio.GetValue(furniture);
            if (!GameHooks.Alive(sound) || (float)mode.GetValue(sound, null) != 1f) return false;
            ShuffleSequence state = State(sound); state.Enabled = !state.Enabled; state.Reset(); enabled = state.Enabled;
            return true;
        }
        private bool CdOn(object furniture) { return GameHooks.Alive(furniture) && (bool)power.GetValue(furniture, null) && (int)furnitureMode.GetValue(furniture) == 1; }
        internal string HoverText(object button, string original)
        {
            object furniture = presetRadio.GetValue(button);
            if ((int)presetIndex.GetValue(button) != 6 || !CdOn(furniture)) return original;
            object sound = radio.GetValue(furniture);
            if (!GameHooks.Alive(sound) || (float)mode.GetValue(sound, null) != 1f) return original;
            ShuffleSequence state;
            bool enabled = PreferredShuffle != null ? PreferredShuffle() : sequences.TryGetValue(sound, out state) && state.Enabled;
            return " 6 - Shuffle " + (enabled ? "ON" : "OFF");
        }
        internal int AutoNext(object manager, int current, int size, object sound) { return State(sound).Next(current, size); }
        internal static int NativeRandom(object manager, int current, int size) { return (int)nativeRandom.Invoke(manager, new object[] { current, size }); }
        internal bool Seek(object manager, object sound, bool previous, out bool changed)
        {
            changed = false;
            if (!GameHooks.Alive(sound) || (float)mode.GetValue(sound, null) != 1f) return true;
            ShuffleSequence state = State(sound);
            if (!state.Enabled) return true;
            IList tracks = (IList)library.GetValue(manager);
            int current = (int)index.GetValue(sound, null);
            if (tracks == null || tracks.Count == 0 || current < 0 || current >= tracks.Count) return true;
            float seconds = (float)time.GetValue(sound, null);
            int selected = previous ? state.Previous(current, tracks.Count, seconds) : state.Next(current, tracks.Count);
            changed = true;
            index.SetValue(sound, selected, null); time.SetValue(sound, 0f, null);
            select.Invoke(manager, new[] { sound });
            return false;
        }
        internal IEnumerable<CodeInstruction> RewriteTick(IEnumerable<CodeInstruction> source)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(source);
            MethodInfo getter = index.GetGetMethod(), setter = index.GetSetMethod();
            int found = -1, count = 0;
            for (int i = 6; i + 1 < code.Count; i++)
            {
                if (!code[i].Calls(randomSong) || !code[i+1].Calls(setter)) continue;
                if (code[i-6].opcode != OpCodes.Ldarg_0 || !IsLocalLoad(code[i-5]) || !code[i-4].Calls(getter)
                    || code[i-3].opcode != OpCodes.Ldarg_0 || !code[i-2].LoadsField(library)
                    || !code[i-1].Calls(typeof(List<AudioClip>).GetProperty("Count").GetGetMethod())) continue;
                // The original random call must have no incoming label/exception boundary that could bypass the inserted argument.
                if (code[i].labels.Count != 0 || code[i].blocks.Count != 0) continue;
                found = i; count++;
            }
            if (count != 1) throw new InvalidOperationException("Expected exactly one CD auto-advance IL sequence; found " + count + ". Shuffle disabled.");
            CodeInstruction radioLoad = new CodeInstruction(code[found-5].opcode, code[found-5].operand);
            code[found] = new CodeInstruction(OpCodes.Call, typeof(Plugin).GetMethod("ChooseAutomatic", BindingFlags.Static | BindingFlags.NonPublic));
            code.Insert(found, radioLoad);
            return code;
        }
        private static bool IsLocalLoad(CodeInstruction code)
        {
            return code.opcode == OpCodes.Ldloc || code.opcode == OpCodes.Ldloc_S || code.opcode == OpCodes.Ldloc_0
                || code.opcode == OpCodes.Ldloc_1 || code.opcode == OpCodes.Ldloc_2 || code.opcode == OpCodes.Ldloc_3;
        }
    }
}
