using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.HomeScreenSections.Helpers;

/// <summary>
/// The few places the plugin reaches a non-public Jellyfin member by name. <c>Type.GetMethod/GetProperty</c> with
/// <c>NonPublic</c> only sees members declared on the type itself, not a private member of a base class, and the runtime
/// type is often not the one that declares it (CoreAppHost : ApplicationHost) or is a decorator another plugin put in
/// front of the real service (Gelato's <c>ICollectionManager</c>). Both silently returned nothing.
/// </summary>
public static class ReflectionHelper
{
    private const BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags DeclaredInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;

    /// <summary>A private/protected instance method by name, searched up the base-class chain. Null if no type has it.</summary>
    public static MethodInfo? FindNonPublicMethod(Type type, string name, params Type[] parameterTypes)
    {
        for (Type? t = type; t != null; t = t.BaseType)
        {
            MethodInfo? method = t.GetMethod(name, DeclaredInstance, null, parameterTypes, null);
            if (method != null)
            {
                return method;
            }
        }

        return null;
    }

    /// <summary>A private/protected instance property by name, searched up the base-class chain. Null if no type has it.</summary>
    public static PropertyInfo? FindNonPublicProperty(Type type, string name)
    {
        for (Type? t = type; t != null; t = t.BaseType)
        {
            PropertyInfo? property = t.GetProperty(name, DeclaredInstance);
            if (property != null)
            {
                return property;
            }
        }

        return null;
    }

    /// <summary>The host's configured public URL (<c>ApplicationHost.PublishedServerUrl</c>, private), or null.</summary>
    public static string? GetPublishedServerUrl(object? applicationHost)
    {
        if (applicationHost == null)
        {
            return null;
        }

        try
        {
            return FindNonPublicProperty(applicationHost.GetType(), "PublishedServerUrl")?.GetValue(applicationHost) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The collections the user can see, i.e. what <c>CollectionManager.GetCollections(User)</c> (private) returns.
    /// 1. that method on the manager (searched up its base classes);
    /// 2. else, when the manager is a decorator, the same on the manager it wraps (found by an <see cref="ICollectionManager"/>
    ///    typed field, any depth);
    /// 3. else the public route the private method itself takes: the collections folder's BoxSet children.
    /// </summary>
    public static IEnumerable<BoxSet> GetCollections(ICollectionManager collectionManager, User user)
    {
        IEnumerable<BoxSet>? viaMethod = TryPrivateGetCollections(collectionManager, user, 0);
        if (viaMethod != null)
        {
            return viaMethod;
        }

        Folder? folder = collectionManager.GetCollectionsFolder(false).GetAwaiter().GetResult();
        return folder == null ? Enumerable.Empty<BoxSet>() : folder.GetChildren(user, true, null).OfType<BoxSet>().ToList();
    }

    private static IEnumerable<BoxSet>? TryPrivateGetCollections(object manager, User user, int depth)
    {
        MethodInfo? method = FindNonPublicMethod(manager.GetType(), "GetCollections", typeof(User));
        if (method != null)
        {
            return method.Invoke(manager, new object?[] { user }) as IEnumerable<BoxSet> ?? Enumerable.Empty<BoxSet>();
        }

        if (depth >= 4)
        {
            return null;
        }

        for (Type? t = manager.GetType(); t != null; t = t.BaseType)
        {
            foreach (FieldInfo field in t.GetFields(DeclaredInstance))
            {
                if (!typeof(ICollectionManager).IsAssignableFrom(field.FieldType) || field.GetValue(manager) is not { } inner || ReferenceEquals(inner, manager))
                {
                    continue;
                }

                IEnumerable<BoxSet>? result = TryPrivateGetCollections(inner, user, depth + 1);
                if (result != null)
                {
                    return result;
                }
            }
        }

        return null;
    }
}
