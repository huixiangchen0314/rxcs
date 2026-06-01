// Rx.cs
// 轻量级响应式状态管理库 (C# 移植版)
// - 核心类型 ARef<T> / AComputed<T> / AEffect
// - watchEffect / watch
// - ReactiveObject 基类（手动属性注册，强类型）
// - Reactive<T> 自动代理（反射 + 表达式树，无 DynamicObject）

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;


namespace Rxcs
{
    public static class Rx
    {
        [ThreadStatic]
        private static AEffect _activeEffect;
        private static readonly Stack<AEffect> EffectStack = new Stack<AEffect>();

        // ==================== ARef<T> ====================
        public sealed class ARef<T> : IRef
        {
            private T _value;
            private List<AEffect> _deps;

            public ARef(T initial) => _value = initial;

            public T Value
            {
                get
                {
                    if (_activeEffect != null) _activeEffect.AddDep(this);
                    return _value;
                }
                set
                {
                    if (EqualityComparer<T>.Default.Equals(_value, value)) return;
                    _value = value;
                    Notify();
                }
            }

            private void Notify()
            {
                if (_deps == null) return;
                var copy = _deps.ToArray();
                foreach (var e in copy) e.Run();
            }

            void IRef.AddDep(AEffect e) => AddDep(e);
            void IRef.RemoveDep(AEffect e) => RemoveDep(e);

            internal void AddDep(AEffect effect)
            {
                _deps ??= new List<AEffect>();
                if (!_deps.Contains(effect)) _deps.Add(effect);
            }

            internal void RemoveDep(AEffect effect) => _deps?.Remove(effect);
        }

        // ==================== AComputed<T> ====================
        public sealed class AComputed<T>
        {
            private readonly Func<T> _getter;
            public AComputed(Func<T> getter) => _getter = getter;
            public T Get() => _getter(); // 无缓存，每次求值
        }

        // ==================== AEffect ====================
        internal class AEffect
        {
            private readonly Action _action;
            private readonly List<IRef> _deps = new List<IRef>();
            private bool _running;

            public AEffect(Action action) => _action = action;

            public void Run()
            {
                if (_running) return;
                _running = true;
                Cleanup();

                var prev = _activeEffect;
                _activeEffect = this;
                EffectStack.Push(this);

                try { _action(); }
                catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
                finally
                {
                    EffectStack.Pop();
                    _activeEffect = prev;
                    _running = false;
                }
            }

            private void Cleanup()
            {
                foreach (var dep in _deps) dep.RemoveDep(this);
                _deps.Clear();
            }

            public void AddDep(IRef dep)
            {
                if (!_deps.Contains(dep))
                {
                    _deps.Add(dep);
                    dep.AddDep(this);
                }
            }

            public void Stop() => Cleanup();
        }

        internal interface IRef
        {
            void AddDep(AEffect effect);
            void RemoveDep(AEffect effect);
        }

        // ==================== 公共 API ====================
        public static ARef<T> Ref<T>(T initial) => new ARef<T>(initial);
        public static AComputed<T> Computed<T>(Func<T> getter) => new AComputed<T>(getter);

        public static IDisposable WatchEffect(Action action)
        {
            var effect = new AEffect(action);
            effect.Run();
            return new DisposableAction(effect.Stop);
        }

        public static IDisposable Watch<T>(ARef<T> source, Action<T, T> callback, bool immediate = false)
        {
            T oldValue = source.Value;
            if (immediate) callback(oldValue, default);

            return WatchEffect(() =>
            {
                T newValue = source.Value;
                if (!EqualityComparer<T>.Default.Equals(newValue, oldValue))
                {
                    callback(newValue, oldValue);
                    oldValue = newValue;
                }
            });
        }

        // ==================== ReactiveObject 基类（手动模式） ====================
        /// <summary>
        /// 继承此类即可手动注册响应式属性（推荐方式，无动态调用）。
        /// 使用示例：
        /// public class MyModel : ReactiveObject {
        ///     public ARef<string> Name => Property<string>("Name");
        ///     public ARef<int> Age => Property<int>("Age");
        /// }
        /// </summary>
        public class ReactiveObject
        {
            private readonly Dictionary<string, object> _refs = new Dictionary<string, object>();

            /// <summary> 获取或创建响应式属性包装 </summary>
            protected ARef<T> Property<T>(string propertyName)
            {
                if (!_refs.TryGetValue(propertyName, out var existing))
                {
                    var r = new ARef<T>(default);
                    _refs[propertyName] = r;
                    return r;
                }
                return (ARef<T>)existing;
            }

            /// <summary> 直接获取已存在的响应式属性，若不存在返回 null </summary>
            protected ARef<T> GetProperty<T>(string propertyName)
            {
                if (_refs.TryGetValue(propertyName, out var val) && val is ARef<T> r)
                    return r;
                return null;
            }

            /// <summary> 获取所有已注册的响应式属性名称 </summary>
            public IEnumerable<string> GetPropertyNames()
            {
                return _refs.Keys;
            }
        }

        // ==================== 自动代理：Reactive<T> ====================
        /// <summary>
        /// 将任意 POCO 对象包装为透明响应式代理。
        /// 使用反射 + 表达式树动态生成代理类型，所有属性读写均转为 ARef<T>。
        /// 注意：代理对象与原对象类型不同，需通过代理访问才能触发响应。
        /// </summary>
        public static T Reactive<T>(T obj) where T : class
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            var type = typeof(T);
            if (!type.IsClass || type.IsSealed)
                throw new InvalidOperationException("Reactive<T> only supports non-sealed classes.");

            var proxyType = ReactiveProxyCache.GetOrCreateProxy(type);
            var proxy = (T)Activator.CreateInstance(proxyType);

            // 将原对象的所有可读属性值复制到代理的 ARef 中
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || !prop.CanWrite) continue;
                var value = prop.GetValue(obj);
                // 通过代理内部的 ARef 字典设置初始值
                var refField = proxyType.GetField($"__ref_{prop.Name}", BindingFlags.NonPublic | BindingFlags.Instance);
                if (refField != null)
                {
                    var aref = refField.GetValue(proxy);
                    // 构造泛型 SetValue 调用
                    var setMethod = aref.GetType().GetProperty("Value")?.GetSetMethod();
                    setMethod?.Invoke(aref, new[] { value });
                }
            }

            return proxy;
        }

        /// <summary> 检查对象是否为响应式代理 </summary>
        public static bool IsReactive(object obj) => obj != null && obj.GetType().GetCustomAttribute<ReactiveProxyAttribute>() != null;

        // ==================== 内部工具 ====================
        internal class DisposableAction : IDisposable
        {
            private readonly Action _action;
            public DisposableAction(Action action) => _action = action;
            public void Dispose() => _action?.Invoke();
        }

        [AttributeUsage(AttributeTargets.Class)]
        internal class ReactiveProxyAttribute : Attribute { }

        internal static class ReactiveProxyCache
        {
            private static readonly Dictionary<Type, Type> _cache = new Dictionary<Type, Type>();
            private static readonly ModuleBuilder _moduleBuilder;

            static ReactiveProxyCache()
            {
                var assemblyName = new AssemblyName("RxProxies");
                var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
                var moduleBuilder = assemblyBuilder.DefineDynamicModule("RxProxiesModule");
                _moduleBuilder = moduleBuilder;
            }

            public static Type GetOrCreateProxy(Type baseType)
            {
                if (_cache.TryGetValue(baseType, out var cached))
                    return cached;

                var typeBuilder = _moduleBuilder.DefineType(
                    $"{baseType.Name}_RxProxy",
                    TypeAttributes.Public | TypeAttributes.Class,
                    baseType);

                // 添加特性标记
                typeBuilder.SetCustomAttribute(new CustomAttributeBuilder(
                    typeof(ReactiveProxyAttribute).GetConstructor(Type.EmptyTypes), new object[0]));

                // 为每个可读写属性创建 ARef 字段和重写
                var props = baseType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (var prop in props)
                {
                    if (!prop.CanRead || !prop.CanWrite) continue;

                    var propType = prop.PropertyType;
                    var refFieldType = typeof(ARef<>).MakeGenericType(propType);
                    var refField = typeBuilder.DefineField($"__ref_{prop.Name}", refFieldType, FieldAttributes.Private);

                    // 初始化字段在构造函数中（暂无默认值）
                    var ctor = typeBuilder.DefineConstructor(
                        MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
                    var ilCtor = ctor.GetILGenerator();
                    ilCtor.Emit(OpCodes.Ldarg_0);
                    ilCtor.Emit(OpCodes.Call, baseType.GetConstructor(Type.EmptyTypes));
                    ilCtor.Emit(OpCodes.Ret);

                    // 重写 getter
                    var getterMethod = typeBuilder.DefineMethod(
                        $"get_{prop.Name}",
                        MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName,
                        propType, Type.EmptyTypes);
                    var ilGetter = getterMethod.GetILGenerator();
                    ilGetter.Emit(OpCodes.Ldarg_0);
                    ilGetter.Emit(OpCodes.Ldfld, refField);
                    ilGetter.Emit(OpCodes.Callvirt, refFieldType.GetProperty("Value").GetGetMethod());
                    ilGetter.Emit(OpCodes.Ret);

                    // 重写 setter
                    var setterMethod = typeBuilder.DefineMethod(
                        $"set_{prop.Name}",
                        MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName,
                        null, new[] { propType });
                    var ilSetter = setterMethod.GetILGenerator();
                    ilSetter.Emit(OpCodes.Ldarg_0);
                    ilSetter.Emit(OpCodes.Ldfld, refField);
                    ilSetter.Emit(OpCodes.Ldarg_1);
                    ilSetter.Emit(OpCodes.Callvirt, refFieldType.GetProperty("Value").GetSetMethod());
                    ilSetter.Emit(OpCodes.Ret);

                    // 绑定属性
                    var propBuilder = typeBuilder.DefineProperty(prop.Name, PropertyAttributes.None, propType, null);
                    propBuilder.SetGetMethod(getterMethod);
                    propBuilder.SetSetMethod(setterMethod);

                    // 重写基类的虚属性（如果基类是 abstract 或 virtual）
                    if (prop.GetGetMethod().IsVirtual)
                    {
                        typeBuilder.DefineMethodOverride(getterMethod, prop.GetGetMethod());
                    }
                    if (prop.GetSetMethod().IsVirtual)
                    {
                        typeBuilder.DefineMethodOverride(setterMethod, prop.GetSetMethod());
                    }
                }

                var createdType = typeBuilder.CreateType();
                _cache[baseType] = createdType;
                return createdType;
            }
        }
    }
}
