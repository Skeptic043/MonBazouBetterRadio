using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RadioEnhancements
{
    // Native Tick indexes a partially rebuilt library. Synchronize once per observed generation.
    internal sealed class LoadingPlaybackGuard
    {
        internal readonly MethodInfo Tick, SelectPlayback;
        private readonly FieldInfo library, playing;
        private readonly PropertyInfo mode, index, time;
        private readonly ConditionalWeakTable<object, Marker> synchronized = new ConditionalWeakTable<object, Marker>();

        internal LoadingPlaybackGuard()
        {
            Type manager = RequiredType("SOUND_Manager"), radio = RequiredType("SOUND_Radio");
            library = RequiredField(manager, "CDPlayer", typeof(List<AudioClip>));
            playing = RequiredField(manager, "RadioPlaying", typeof(List<>).MakeGenericType(radio));
            mode = RequiredProperty(radio, "GetFMCD", typeof(float), false);
            index = RequiredProperty(radio, "GetCurrentCDSong", typeof(int), true);
            time = RequiredProperty(radio, "GetCurrentCDTime", typeof(float), true);
            Tick = RequiredMethod(manager, "Tick", typeof(int));
            SelectPlayback = RequiredMethod(manager, "PlayStationOnThisRadio", radio);
        }

        internal void BeforeTick(object owner, LoadingCoordinator loading, Action<Exception> report, Func<object, bool> alreadyManaged = null)
        {
            if (!Alive(owner) || loading == null) return;
            LoadingCoordinator.LoadingStatus status = loading.GetStatus(owner);
            if (!status.Exists) return;
            IList clips = (IList)library.GetValue(owner);
            if (clips == null || clips.Count == 0) return;
            IList radios = (IList)playing.GetValue(owner);
            if (radios == null) return;
            object[] activeRadios = new object[radios.Count]; radios.CopyTo(activeRadios, 0);
            foreach (object radio in activeRadios)
            {
                try
                {
                    if (!Alive(radio)) continue;
                    if (alreadyManaged != null && alreadyManaged(radio)) continue;
                    int current = (int)index.GetValue(radio, null);
                    Synchronize(true, (float)mode.GetValue(radio, null), clips.Count, current, !IsSynchronized(owner, radio, status.Generation),
                        delegate(int selected, float position) { index.SetValue(radio, selected, null); time.SetValue(radio, position, null); },
                        delegate { SelectPlayback.Invoke(owner, new[] { radio }); });
                    // Selection postfix marks only when the original actually executes successfully.
                }
                catch (Exception error) { if (report != null) report(error); }
            }
        }

        internal void BeforeSelection(object owner, object radio, LoadingCoordinator loading)
        {
            if (!Alive(owner) || !Alive(radio) || loading == null) return;
            LoadingCoordinator.LoadingStatus status = loading.GetStatus(owner);
            if (!status.Exists) return;
            IList clips = (IList)library.GetValue(owner);
            if (clips == null) return;
            Synchronize(true, (float)mode.GetValue(radio, null), clips.Count, (int)index.GetValue(radio, null), !IsSynchronized(owner, radio, status.Generation),
                delegate(int selected, float position) { index.SetValue(radio, selected, null); time.SetValue(radio, position, null); }, null);
        }

        private bool IsSynchronized(object owner, object radio, long generation)
        {
            Marker marker;
            object markedOwner = null;
            return synchronized.TryGetValue(radio, out marker) && marker.Owner.TryGetTarget(out markedOwner)
                && ReferenceEquals(owner, markedOwner) && marker.Generation == generation;
        }

        internal void AfterSelection(object owner, object radio, LoadingCoordinator loading)
        {
            if (!Alive(owner) || !Alive(radio) || loading == null) return;
            LoadingCoordinator.LoadingStatus status = loading.GetStatus(owner);
            IList clips = (IList)library.GetValue(owner);
            int current = (int)index.GetValue(radio, null);
            if (!status.Exists || clips == null || clips.Count == 0 || (float)mode.GetValue(radio, null) != 1f
                || current < 0 || current >= clips.Count) return;
            synchronized.Remove(radio);
            synchronized.Add(radio, new Marker { Owner = new WeakReference<object>(owner), Generation = status.Generation });
        }

        // Pure decision/action seam shared by the actual reflected Tick/selection paths and tests.
        internal static bool Synchronize(bool observed, float cdMode, int count, int current, bool newGeneration,
            Action<int, float> assign, Action select)
        {
            if (!observed || cdMode != 1f || count <= 0) return false;
            bool invalid = current < 0 || current >= count;
            if (!invalid && !newGeneration) return false;
            assign(invalid ? 0 : current, 0f);
            if (select != null) select();
            return true;
        }

        private sealed class Marker { internal WeakReference<object> Owner; internal long Generation; }
        private static bool Alive(object value)
        {
            UnityEngine.Object unity = value as UnityEngine.Object;
            return value != null && !ReferenceEquals(unity, null) && unity != null;
        }
        private static Type RequiredType(string name)
        {
            Type type = AccessTools.TypeByName(name);
            if (type == null || type.Assembly.GetName().Name != "Assembly-CSharp" || !typeof(MonoBehaviour).IsAssignableFrom(type))
                throw new TypeLoadException("Unsupported loading playback type: " + name);
            return type;
        }
        private static FieldInfo RequiredField(Type type, string name, Type expected)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field == null || field.FieldType != expected || field.IsStatic) throw new MissingFieldException(type.FullName, name);
            return field;
        }
        private static PropertyInfo RequiredProperty(Type type, string name, Type expected, bool writable)
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (property == null || property.PropertyType != expected || property.GetGetMethod() == null
                || property.GetIndexParameters().Length != 0 || (writable && property.GetSetMethod() == null))
                throw new MissingMemberException(type.FullName, name);
            return property;
        }
        private static MethodInfo RequiredMethod(Type type, string name, params Type[] parameters)
        {
            MethodInfo method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null, parameters, null);
            if (method == null || method.ReturnType != typeof(void) || method.ContainsGenericParameters) throw new MissingMethodException(type.FullName, name);
            return method;
        }
    }
}
