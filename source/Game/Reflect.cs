using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;

namespace PerformanceLog
{
    /// <summary>Finding the game's internal types, methods and fields without a compile-time reference to them.</summary>
    internal static class Reflect
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        public static Type GameType(string fullName) => AccessTools.TypeByName(fullName);

        public static MethodInfo Method(string typeName, string methodName)
        {
            Type type = GameType(typeName);
            return type == null ? null : AccessTools.Method(type, methodName);
        }

        /// <summary>The overload of a method with exactly these parameters.</summary>
        public static MethodInfo Method(string typeName, string methodName, params Type[] parameters)
        {
            Type type = GameType(typeName);
            return type == null ? null : AccessTools.Method(type, methodName, parameters);
        }

        public static FieldInfo Field(Type type, string name)
        {
            FieldInfo field = type == null ? null : AccessTools.Field(type, name);
            if (field == null) throw new MissingFieldException(type?.Name, name);
            return field;
        }

        /// <summary>One of this mod's own static methods, public or not, for use as a patch.</summary>
        public static MethodInfo Own(Type type, string name)
        {
            MethodInfo method = type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null) throw new MissingMethodException(type.Name, name);
            return method;
        }

        /// <summary>A compiled reader for a field of a type that cannot be named at compile time.</summary>
        public static Func<object, T> FieldGetter<T>(Type declaringType, string fieldName)
        {
            FieldInfo field = Field(declaringType, fieldName);
            ParameterExpression instance = Expression.Parameter(typeof(object), "instance");
            Expression body = Expression.Convert(Expression.Field(Expression.Convert(instance, declaringType), field), typeof(T));
            return Expression.Lambda<Func<object, T>>(body, instance).Compile();
        }

        /// <summary>A compiled reader for the Count of a collection held in a field.</summary>
        public static Func<object, int> CountOfField(Type declaringType, string fieldName)
        {
            FieldInfo field = Field(declaringType, fieldName);
            PropertyInfo count = field.FieldType.GetProperty("Count");
            if (count == null) throw new MissingMemberException(field.FieldType.Name, "Count");
            ParameterExpression instance = Expression.Parameter(typeof(object), "instance");
            Expression body = Expression.Property(Expression.Field(Expression.Convert(instance, declaringType), field), count);
            return Expression.Lambda<Func<object, int>>(body, instance).Compile();
        }

        /// <summary>True if the method has a catch ... when clause. Harmony cannot regenerate those under Mono, and the attempt leaves a broken dynamic type that crashes the game later.</summary>
        public static bool HasExceptionFilter(MethodBase method)
        {
            MethodBody body = method.GetMethodBody();
            if (body == null) return false;
            foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
                if (clause.Flags == ExceptionHandlingClauseOptions.Filter) return true;
            return false;
        }

        /// <summary>Every overload of a public or non-public method by name, for the Watch list.</summary>
        public static List<MethodBase> Overloads(Type type, string name) =>
            type.GetMethods(Any).Where(m => m.Name == name).Cast<MethodBase>().ToList();
    }
}
