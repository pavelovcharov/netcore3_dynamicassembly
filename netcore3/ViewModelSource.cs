using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;

namespace Generator {
    static class DictionaryExtensions {
        public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, Func<TValue> createValueDelegate) {
            TValue result;
            if(!dictionary.TryGetValue(key, out result)) {
                dictionary[key] = (result = createValueDelegate());
            }
            return result;
        }
    }
    public class ViewModelSource {
        [ThreadStatic]
        static Dictionary<Type, Type> types;
        static Dictionary<Type, Type> Types { get { return types ?? (types = new Dictionary<Type, Type>()); } }

        public static T Create<T>(Expression<Func<T>> constructorExpression) where T : class {
            var actualAxpression = GetCtorExpression(constructorExpression, typeof(T), false);
            return Expression.Lambda<Func<T>>(actualAxpression).Compile()();
        }

        #region helpers
        static Expression GetCtorExpression(LambdaExpression constructorExpression, Type resultType, bool useOnlyParameters) {
            Type type = GetPOCOType(resultType);
            NewExpression newExpression = constructorExpression.Body as NewExpression;
            if(newExpression != null) {
                return GetNewExpression(type, newExpression);
            }
            MemberInitExpression memberInitExpression = constructorExpression.Body as MemberInitExpression;
            if(memberInitExpression != null) {
                return Expression.MemberInit(GetNewExpression(type, memberInitExpression.NewExpression), memberInitExpression.Bindings);
            }
            throw new ArgumentException("constructorExpression");
        }

        static NewExpression GetNewExpression(Type type, NewExpression newExpression) {
            var actualCtor = type.GetConstructor(newExpression.Constructor.GetParameters().Select(x => x.ParameterType).ToArray() ?? Type.EmptyTypes);
            return Expression.New(actualCtor, newExpression.Arguments);
        }


        public static Type GetPOCOType(Type type) {
            Func<Type> createType = () => CreateTypeCore(type);
            return Types.GetOrAdd(type, createType);
        }
        #endregion
        static Type CreateTypeCore(Type type) {
            TypeBuilder typeBuilder = BuilderType.CreateTypeBuilder(type);
            BuilderType.BuildConstructors(type, typeBuilder);
            ImplementISupportServices(type, typeBuilder);
            BuildServiceProperties(type, typeBuilder);
            return typeBuilder.CreateType();
        }

        #region services
        static void ImplementISupportServices(Type type, TypeBuilder typeBuilder) {
            if(typeof(ISupportServices).IsAssignableFrom(type))
                return;
            Expression<Func<ISupportServices, IServiceContainer>> getServiceContainerExpression = x => x.ServiceContainer;
            var getter = ExpressionHelper.GetArgumentPropertyStrict(getServiceContainerExpression).GetGetMethod();
            FieldBuilder serviceContainerField = typeBuilder.DefineField("serviceContainer", typeof(IServiceContainer), FieldAttributes.Private);
            var getServiceContainerMethod = BuildGetServiceContainerMethod(typeBuilder, serviceContainerField, getter.Name);
            typeBuilder.DefineMethodOverride(getServiceContainerMethod, getter);
        }
        static MethodBuilder BuildGetServiceContainerMethod(TypeBuilder type, FieldInfo serviceContainerField, string getServiceContainerMethodName) {
            MethodAttributes methodAttributes =
                  MethodAttributes.Private
                | MethodAttributes.Virtual
                | MethodAttributes.Final
                | MethodAttributes.HideBySig
                | MethodAttributes.NewSlot;
            MethodBuilder method = type.DefineMethod(typeof(ISupportServices).FullName + "." + getServiceContainerMethodName, methodAttributes);

            Expression<Func<ServiceContainer>> serviceContainerCtorExpression = () => new ServiceContainer(null);

            method.SetReturnType(typeof(IServiceContainer));

            ILGenerator gen = method.GetILGenerator();

            gen.DeclareLocal(typeof(IServiceContainer));
            Label returnLabel = gen.DefineLabel();

            gen.Emit(OpCodes.Ldarg_0);
            gen.Emit(OpCodes.Ldfld, serviceContainerField);
            gen.Emit(OpCodes.Dup);
            gen.Emit(OpCodes.Brtrue_S, returnLabel);
            gen.Emit(OpCodes.Pop);
            gen.Emit(OpCodes.Ldarg_0);
            gen.Emit(OpCodes.Ldarg_0);
            gen.Emit(OpCodes.Newobj, ExpressionHelper.GetConstructor(serviceContainerCtorExpression));
            gen.Emit(OpCodes.Dup);
            gen.Emit(OpCodes.Stloc_0);
            gen.Emit(OpCodes.Stfld, serviceContainerField);
            gen.Emit(OpCodes.Ldloc_0);
            gen.MarkLabel(returnLabel);
            gen.Emit(OpCodes.Ret);

            return method;
        }


        static void BuildServiceProperties(Type type, TypeBuilder typeBuilder) {
            var serviceProperties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).ToArray();
            foreach(var propertyInfo in serviceProperties) {
                var getter = BuildGetServicePropertyMethod(typeBuilder, propertyInfo);
                typeBuilder.DefineMethodOverride(getter, propertyInfo.GetGetMethod(true));
                var newProperty = typeBuilder.DefineProperty(propertyInfo.Name, PropertyAttributes.None, propertyInfo.PropertyType, Type.EmptyTypes);
                newProperty.SetGetMethod(getter);
            }
        }
        static MethodBuilder BuildGetServicePropertyMethod(TypeBuilder type, PropertyInfo property) {
            var getMethod = property.GetGetMethod(true);
            MethodAttributes methodAttributes =
                (getMethod.IsPublic ? MethodAttributes.Public : MethodAttributes.Family)
                | MethodAttributes.Virtual
                | MethodAttributes.HideBySig;
            MethodBuilder method = type.DefineMethod(getMethod.Name, methodAttributes);
            method.SetReturnType(property.PropertyType);

            ILGenerator gen = method.GetILGenerator();
            Expression<Func<ISupportServices, IServiceContainer>> serviceContainerPropertyExpression = x => x.ServiceContainer;
            Type[] getServiceMethodParams = new Type[] { typeof(string), typeof(object) };
            MethodInfo getServiceMethod =
                typeof(IServiceContainer).GetMethod("GetService", BindingFlags.Instance | BindingFlags.Public, null,
                    new Type[] { typeof(string), typeof(object) }, null);
            getServiceMethod = getServiceMethod.MakeGenericMethod(property.PropertyType);

            gen.Emit(OpCodes.Ldarg_0);
            gen.Emit(OpCodes.Callvirt, ExpressionHelper.GetArgumentPropertyStrict(serviceContainerPropertyExpression).GetGetMethod());
            gen.Emit(OpCodes.Ldnull);
            gen.Emit(OpCodes.Ldc_I4_S, (int)0);
            gen.Emit(OpCodes.Call, getServiceMethod);
            gen.Emit(OpCodes.Ret);
            return method;
        }
        #endregion
    }
    static class BuilderType {
        public static TypeBuilder CreateTypeBuilder(Type baseType) {
            ModuleBuilder module = GetModuleBuilder(baseType.Assembly);
            string typeName = baseType.Name + "_" + Guid.NewGuid().ToString().Replace('-', '_');
            return module.DefineType(typeName, TypeAttributes.Public, baseType, new Type[] { typeof(ISupportServices) });
        }

        public static void BuildConstructors(Type type, TypeBuilder typeBuilder) {
            var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(x => BuilderCommon.CanAccessFromDescendant(x)).ToArray();
            foreach(ConstructorInfo constructor in ctors) {
                BuildConstructor(typeBuilder, constructor);
            }
        }
        static ConstructorBuilder BuildConstructor(TypeBuilder type, ConstructorInfo baseConstructor) {
            var parameters = baseConstructor.GetParameters();
            ConstructorBuilder method = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, parameters.Select(x => x.ParameterType).ToArray());
            for(int i = 0; i < parameters.Length; i++)
                method.DefineParameter(i + 1, parameters[i].Attributes, parameters[i].Name);
            ILGenerator gen = method.GetILGenerator();
            gen.Emit(OpCodes.Ldarg_0);
            for(int i = 0; i < parameters.Length; i++)
                gen.Emit(OpCodes.Ldarg_S, i + 1);
            gen.Emit(OpCodes.Call, baseConstructor);
            gen.Emit(OpCodes.Ret);
            return method;
        }
        [ThreadStatic]
        static Dictionary<Assembly, ModuleBuilder> builders;
        static Dictionary<Assembly, ModuleBuilder> Builders {
            get { return builders ?? (builders = new Dictionary<Assembly, ModuleBuilder>()); }
        }
        static ModuleBuilder GetModuleBuilder(Assembly assembly) {
            return Builders.GetOrAdd(assembly, () => CreateBuilder());
        }
        static ModuleBuilder CreateBuilder() {
            var assemblyName = new AssemblyName();
            assemblyName.Name = "CustomAssembly.DynamicTypes." + Guid.NewGuid().ToString();
            var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            return assemblyBuilder.DefineDynamicModule(assemblyName.Name/*, assemblyName.Name + ".dll"*//*, false*/);
        }
    }
    static class BuilderCommon {
        public static bool CanAccessFromDescendant(MethodBase method) {
            return method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;
        }
    }

    static class ExpressionHelper {
        internal static PropertyInfo GetArgumentPropertyStrict<T, TResult>(Expression<Func<T, TResult>> expression) {
            MemberExpression memberExpression = null;
            if(expression.Body is MemberExpression) {
                memberExpression = (MemberExpression)expression.Body;
            }
            else if(expression.Body is UnaryExpression) {
                UnaryExpression uExp = (UnaryExpression)expression.Body;
                if(uExp.NodeType == ExpressionType.Convert)
                    memberExpression = (MemberExpression)uExp.Operand;
            }
            if(memberExpression == null)
                throw new ArgumentException("expression");
            return (PropertyInfo)memberExpression.Member;
        }
        internal static ConstructorInfo GetConstructor<T>(Expression<Func<T>> commandMethodExpression) {
            return GetConstructorCore(commandMethodExpression);
        }
        internal static ConstructorInfo GetConstructorCore(LambdaExpression commandMethodExpression) {
            NewExpression newExpression = commandMethodExpression.Body as NewExpression;
            if(newExpression == null) {
                throw new ArgumentException("commandMethodExpression");
            }
            return newExpression.Constructor;
        }
    }


    public interface ISupportServices {
        IServiceContainer ServiceContainer { get; }
    }
    public interface IServiceContainer {
        T GetService<T>(string key, object arg) where T : class;
    }
    public class ServiceContainer : IServiceContainer {
        public ServiceContainer(object owner) { }
        T IServiceContainer.GetService<T>(string key, object arg) {
            System.Windows.MessageBox.Show("!");
            return null;
        }
    }
}
