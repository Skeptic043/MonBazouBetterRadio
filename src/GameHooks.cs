using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace RadioEnhancements
{
    internal sealed class GameHooks
    {
        internal readonly MethodInfo BuildLibrary, LoadMusic, UpdateScreen, SelectPlayback;
        private readonly FieldInfo library, singleton, screen, radio, speakers, furnitureMode, overlay;
        private readonly PropertyInfo powered, mode, index, furnitureForRadio;
        private readonly DisplayOwnership ownership = new DisplayOwnership();
        private readonly List<DisplayState> displays = new List<DisplayState>();
        private sealed class DisplayState
        {
            internal WeakReference Furniture, Owner, Clip;
            internal string Title, Written, NativeLabel, Status;
            internal float Started, StatusUntil;
            internal TitleMarquee Marquee;
        }
        internal GameHooks()
        {
            Type manager = RequiredType("SOUND_Manager"), furniture = RequiredType("Furniture_Radio"), soundRadio = RequiredType("SOUND_Radio");
            BuildLibrary = Method(manager, "BuildPlayerMusicList", typeof(void));
            LoadMusic = Method(manager, "LoadMusic", typeof(IEnumerator), typeof(string), typeof(AudioType));
            UpdateScreen = Method(furniture, "UpdateScreen", typeof(void));
            SelectPlayback = Method(manager, "PlayStationOnThisRadio", typeof(void), soundRadio);
            library = Field(manager, "CDPlayer", typeof(List<AudioClip>), false);
            screen = Field(furniture, "ScreenText", typeof(TextMeshPro), false);
            radio = Field(furniture, "SOUND_Radio", soundRadio, false);
            furnitureMode = Field(furniture, "FMCD", typeof(int), false);
            overlay = Field(furniture, "CoroutineScreenRunning", typeof(bool), false);
            speakers = Field(soundRadio, "Speakers", typeof(AudioSource[]), false);
            powered = Property(furniture, "Get_IsOn", typeof(bool));
            mode = Property(soundRadio, "GetFMCD", typeof(float));
            index = Property(soundRadio, "GetCurrentCDSong", typeof(int));
            furnitureForRadio = Property(soundRadio, "Get_Radio", furniture);
            Type parent = manager.BaseType;
            if (parent == null || !parent.IsGenericType || parent.GetGenericTypeDefinition().FullName != "Singleton`1" || parent.GetGenericArguments()[0] != manager)
                throw new MissingMemberException("SOUND_Manager singleton base changed.");
            singleton = Field(parent, "m_Instance", manager, true);
        }
        internal IList Library(object owner) { return (IList)library.GetValue(owner); }
        internal object ExistingManager { get { return singleton.GetValue(null); } }
        internal Func<object, object, AudioClip[], Tuple<object, string>> CustomTitle;
        private string NativeLabel(object sound) { return "CD TRACK " + ((int)index.GetValue(sound, null) + 1).ToString("00"); }
        private bool Resolve(object furniture, MusicLibraryTracker tracker, out object owner, out object clip, out TextMeshPro text, out string title)
        {
            owner = null; clip = null; text = null; title = null;
            if (!Alive(furniture) || !(bool)powered.GetValue(furniture, null) || (int)furnitureMode.GetValue(furniture) != 1 || (bool)overlay.GetValue(furniture)) return false;
            owner = singleton.GetValue(null);
            object sound = radio.GetValue(furniture);
            text = (TextMeshPro)screen.GetValue(furniture);
            if (!Alive(owner) || !Alive(sound) || text == null) return false;
            AudioSource[] sources = (AudioSource[])speakers.GetValue(sound);
            if (sources == null || sources.Length == 0) return false;
            object[] clips = new object[sources.Length];
            for (int i = 0; i < sources.Length; ++i) { if (sources[i] == null || sources[i].clip == null) return false; clips[i] = sources[i].clip; }
            if (CustomTitle != null)
            {
                AudioClip[] actual = new AudioClip[sources.Length];
                for (int i = 0; i < sources.Length; ++i) actual[i] = sources[i].clip;
                Tuple<object, string> custom = CustomTitle(owner, sound, actual);
                if (custom != null) { clip = custom.Item1; title = custom.Item2; return true; }
            }
            bool valid = TrackDisplayController.TryResolve(tracker, owner, Library(owner), true, (float)mode.GetValue(sound, null),
                (int)index.GetValue(sound, null), NativeLabel(sound), clips, out title);
            if (valid) clip = clips[0];
            return valid;
        }
        private DisplayState Find(object furniture) { return displays.Find(delegate(DisplayState state) { return ReferenceEquals(state.Furniture.Target, furniture); }); }
        internal void Display(object furniture, MusicLibraryTracker tracker)
        {
            DisplayState previous = Find(furniture);
            ownership.Forget(furniture);
            object owner, clip; TextMeshPro text; string title;
            if (!Resolve(furniture, tracker, out owner, out clip, out text, out title)) { if (previous != null) displays.Remove(previous); return; }
            string native = NativeLabel(radio.GetValue(furniture));
            if (!String.Equals(text.text, native, StringComparison.Ordinal)) { if (previous != null) displays.Remove(previous); return; }
            DisplayState state = previous;
            if (state == null || !ReferenceEquals(state.Clip.Target, clip) || state.Title != title)
            {
                if (state != null) displays.Remove(state);
                float width = text.rectTransform.rect.width - text.margin.x - text.margin.z;
                if (Single.IsNaN(width) || Single.IsInfinity(width) || width <= 0) width = Measure(text, native);
                state = new DisplayState { Furniture = new WeakReference(furniture), Owner = new WeakReference(owner), Clip = new WeakReference(clip),
                    Title = title, Started = Time.unscaledTime, Marquee = new TitleMarquee(title, width, delegate(string value) { return Measure(text, value); }) };
                displays.Add(state);
            }
            state.NativeLabel = native;
            state.Written = null; // Native redraw returned the screen to us, even when its text matches a filename.
            Write(state, text, Time.unscaledTime);
        }
        private static float Measure(TextMeshPro text, string value) { return text.GetPreferredValues(value, Single.PositiveInfinity, Single.PositiveInfinity).x; }
        private void Write(DisplayState state, TextMeshPro text, float now)
        {
            string value = state.Status != null && now < state.StatusUntil ? state.Status : state.Marquee.Frame(now - state.Started);
            if (text.text == value && state.Written == value) return;
            if (text.text != value) text.text = value;
            state.Written = value;
            ownership.Record(state.Furniture.Target, state.Owner.Target, value, state.NativeLabel);
        }
        internal void ShowShuffle(object furniture, bool enabled)
        {
            DisplayState state = Find(furniture);
            if (state == null || !Alive(furniture) || (bool)overlay.GetValue(furniture)) return;
            TextMeshPro text = (TextMeshPro)screen.GetValue(furniture);
            if (text == null || text.text != state.Written) return;
            state.Status = enabled ? "SHUFFLE ON" : "SHUFFLE OFF"; state.StatusUntil = Time.unscaledTime + 1.4f;
            state.Started = state.StatusUntil;
            Write(state, text, Time.unscaledTime);
        }
        internal void UpdateDisplays(MusicLibraryTracker tracker, Action<Exception> report)
        {
            for (int i = displays.Count - 1; i >= 0; i--)
            {
                DisplayState state = displays[i]; object furniture = state.Furniture.Target;
                try
                {
                    TextMeshPro current = Alive(furniture) ? (TextMeshPro)screen.GetValue(furniture) : null;
                    object owner, clip; TextMeshPro text; string title;
                    if (current == null || current.text != state.Written || !Resolve(furniture, tracker, out owner, out clip, out text, out title))
                    {
                        Release(state, current, true); displays.RemoveAt(i); continue;
                    }
                    if (!ReferenceEquals(state.Clip.Target, clip) || state.Title != title)
                    {
                        // Playback identity changed without a native redraw: discard the old title immediately.
                        current.text = NativeLabel(radio.GetValue(furniture));
                        ownership.Forget(furniture); displays.RemoveAt(i); Display(furniture, tracker); continue;
                    }
                    Write(state, text, Time.unscaledTime);
                }
                catch (Exception error) { Release(state, null, false); displays.RemoveAt(i); report(error); }
            }
        }
        private void Release(DisplayState state, TextMeshPro text, bool restore)
        {
            if (restore && text != null && text.text == state.Written) text.text = state.NativeLabel;
            ownership.Forget(state.Furniture.Target);
        }
        internal void RestoreDisplays()
        {
            foreach (DisplayState state in displays)
            {
                object furniture = state.Furniture.Target;
                if (Alive(furniture)) Release(state, (TextMeshPro)screen.GetValue(furniture), true);
            }
            displays.Clear();
        }
        internal void InvalidateDisplays(object owner, Action<Exception> report)
        {
            for (int i = displays.Count - 1; i >= 0; i--)
            {
                DisplayState state = displays[i]; if (!ReferenceEquals(state.Owner.Target, owner)) continue;
                try { object furniture = state.Furniture.Target; Release(state, Alive(furniture) ? (TextMeshPro)screen.GetValue(furniture) : null, true); }
                catch (Exception error) { report(error); }
                displays.RemoveAt(i);
            }
        }
        internal void RefreshAfterSelection(object owner, object soundRadio)
        { RefreshAfterSelection(owner, soundRadio, false); }
        internal void RefreshAfterSelection(object owner, object soundRadio, bool sharedSelection)
        {
            if (!Alive(owner) || !ReferenceEquals(singleton.GetValue(null), owner) || !Alive(soundRadio)) return;
            object furniture = furnitureForRadio.GetValue(soundRadio, null);
            if (!Alive(furniture) || (bool)overlay.GetValue(furniture)) return;
            TextMeshPro text = (TextMeshPro)screen.GetValue(furniture); if (text == null) return;
            string fallback; bool owned = ownership.TryGetFallback(furniture, text.text, out fallback);
            bool nativeSharedLabel = sharedSelection && DisplayOwnership.IsNativeCdLabel(text.text);
            if (DisplayOwnership.CanRefresh((bool)powered.GetValue(furniture, null), (int)furnitureMode.GetValue(furniture) == 1 && (float)mode.GetValue(soundRadio, null) == 1f,
                text.text, NativeLabel(soundRadio), owned || nativeSharedLabel)) UpdateScreen.Invoke(furniture, null);
        }
        internal static bool Alive(object value) { UnityEngine.Object unity = value as UnityEngine.Object; return value != null && !ReferenceEquals(unity, null) && unity != null; }
        internal static Type RequiredType(string name)
        {
            Type type = AccessTools.TypeByName(name);
            if (type == null || type.Assembly.GetName().Name != "Assembly-CSharp" || !typeof(MonoBehaviour).IsAssignableFrom(type)) throw new TypeLoadException("Unsupported game type: " + name);
            return type;
        }
        internal static MethodInfo Method(Type type, string name, Type result, params Type[] parameters)
        {
            MethodInfo method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, parameters, null);
            if (method == null || method.ReturnType != result || method.ContainsGenericParameters) throw new MissingMethodException(type.FullName, name);
            return method;
        }
        internal static FieldInfo Field(Type type, string name, Type fieldType, bool isStatic)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (field == null || field.FieldType != fieldType || field.IsStatic != isStatic) throw new MissingFieldException(type.FullName, name);
            return field;
        }
        internal static PropertyInfo Property(Type type, string name, Type result)
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (property == null || property.PropertyType != result || property.GetGetMethod() == null || property.GetIndexParameters().Length != 0) throw new MissingMemberException(type.FullName, name);
            return property;
        }
    }
}
