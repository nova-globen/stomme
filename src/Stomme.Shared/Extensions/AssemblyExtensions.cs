using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using Stomme.Modules;
using Stomme.Services;
using Stomme.Shared;
using Stomme.Themes;

// ReSharper disable once CheckNamespace
namespace System.Reflection
{
    public static class AssemblyExtensions
    {
        public static IEnumerable<Type> GetInterfaces<TInterfaceType>(this Assembly assembly)
        {
            if (assembly is null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            return assembly.GetTypes(typeof(TInterfaceType));
        }

        public static IEnumerable<Type> GetTypes(this Assembly assembly, Type interfaceType)
        {
            if (assembly is null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            if (interfaceType is null)
            {
                throw new ArgumentNullException(nameof(interfaceType));
            }

            return assembly.GetTypes()
                .Where(x => !x.IsInterface && !x.IsAbstract && interfaceType.IsAssignableFrom(x));
        }

        public static IEnumerable<Type> GetTypes<T>(this Assembly assembly)
        {
            return assembly.GetTypes(typeof(T));
        }

        public static IEnumerable<T> GetInstances<T>(this Assembly assembly) where T : class
        {
            if (assembly is null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }
            var type = typeof(T);
            var list = assembly.GetTypes()
                .Where(x => !x.IsInterface && !x.IsAbstract && !x.IsGenericType && type.IsAssignableFrom(x));

            foreach (var type1 in list)
            {
                if (Activator.CreateInstance(type1) is T instance) yield return instance;
            }
        }

        public static bool IsStommeAssembly(this Assembly assembly)
        {
            return assembly.FullName != null && (assembly.FullName.Contains("Stomme", StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsStommeAssembly(this FileInfo fileInfo)
        {
            return (fileInfo.Name.Contains("Stomme", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// True when the assembly declares <see cref="StommeFrameworkLibraryAttribute"/>, i.e. it is a
        /// framework building-block library that wires its own DI explicitly and must NOT be subject to the
        /// module/theme convention scan. See <see cref="StommeFrameworkLibraryAttribute"/> for why.
        /// </summary>
        public static bool IsStommeFrameworkLibrary(this Assembly assembly)
        {
            if (assembly is null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            // Matched by full name rather than typeof: the leaf libraries that need to opt out do not
            // reference Stomme.Shared, so each declares its own internal attribute of the same name.
            // GetCustomAttributesData reads metadata without needing the attribute type to load.
            try
            {
                return assembly.GetCustomAttributesData()
                    .Any(a => a.AttributeType.FullName == StommeFrameworkLibraryAttribute.TypeFullName);
            }
            catch (TypeLoadException)
            {
                return false;
            }
        }

        public static IEnumerable<Assembly> GetStommeAssemblies(this AppDomain appDomain)
        {
            return appDomain.GetAssemblies().Where(a => a.IsStommeAssembly());
        }

        public static IEnumerable<Assembly> GetStommeClientAssemblies(this AppDomain appDomain)
        {
            return appDomain.GetStommeAssemblies()
                .Where(a => a.GetTypes<IModuleControl>().Any() || a.GetTypes<IThemeControl>().Any() || a.GetTypes<IClientStartup>().Any())
                .Where(a => Utilities.GetFullTypeName(a.GetName().Name) != Constants.ClientId);
        }

        /// <summary>
        /// Checks if type should be ignored by stomme dynamic loader
        /// </summary>
        /// <param name="type">Checked type</param>
        /// <returns></returns>
        public static bool IsStommeIgnore(this Type type)
        {
            return Attribute.IsDefined(type, typeof(StommeIgnoreAttribute)) || type.IsAbstract || type.IsGenericType;
        }

        public static void LoadStommeAssembly(this AssemblyLoadContext loadContext, FileInfo dll)
        {
            AssemblyName assemblyName = null;
            try
            {
                assemblyName = AssemblyName.GetAssemblyName(dll.FullName);
            }
            catch
            {
                Debug.WriteLine($"Stomme Error: Cannot Get Assembly Name For {dll.Name}");
            }

            loadContext.LoadStommeAssembly(dll, assemblyName);
        }

        public static void LoadStommeAssembly(this AssemblyLoadContext loadContext, FileInfo dll, AssemblyName assemblyName)
        {
            try
            {
                var pdb = Path.ChangeExtension(dll.FullName, ".pdb");
                Assembly assembly = null;

                // load assembly ( and symbols ) from stream to prevent locking files ( as long as dependencies are in /bin they will load as well )
                if (File.Exists(pdb))
                {
                    assembly = loadContext.LoadFromStream(new MemoryStream(File.ReadAllBytes(dll.FullName)), new MemoryStream(File.ReadAllBytes(pdb)));
                }
                else
                {
                    assembly = loadContext.LoadFromStream(new MemoryStream(File.ReadAllBytes(dll.FullName)));
                }
                Debug.WriteLine($"Stomme Info: Loaded Assembly {assemblyName}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Stomme Error: Unable To Load Assembly {assemblyName} - {ex}");
            }
        }
    }
}
