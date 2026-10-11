using System;
using System.Collections.Generic;
using System.Reflection;

namespace ThiefSim2Multiplayer.Game
{
    internal static class NativeBooleanMember
    {
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Dictionary<Type, Dictionary<string, Func<object, bool>>> accessors = new Dictionary<Type, Dictionary<string, Func<object, bool>>>();

        internal static bool Read(object target, string name)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            var type = target.GetType();
            if (!accessors.TryGetValue(type, out var members)) accessors.Add(type, members = new Dictionary<string, Func<object, bool>>());
            if (!members.TryGetValue(name, out var read))
            {
                var property = type.GetProperty(name, InstanceMembers);
                if (property != null && property.PropertyType == typeof(bool) && property.GetIndexParameters().Length == 0 && property.GetGetMethod(true) != null)
                    read = value => (bool)property.GetValue(value, null);
                else
                {
                    var field = type.GetField(name, InstanceMembers);
                    if (field == null || field.FieldType != typeof(bool)) throw new MissingMemberException(type.FullName, name);
                    read = value => (bool)field.GetValue(value);
                }
                members.Add(name, read);
            }
            return read(target);
        }
    }
}
