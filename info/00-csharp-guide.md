# C# — A Guide for Rust and C Developers

---

## 1. The Runtime — CLR and JIT

C# runs on the **Common Language Runtime (CLR)**, which is analogous to the JVM for Java. Your source code compiles to **CIL** (Common Intermediate Language) — a bytecode — which the CLR then **JIT-compiles** to native machine code at runtime.

```
C# source → mcs/dotnet build → CIL bytecode (.exe/.dll) → CLR JIT → native code
```

Compare this to:
- **C**: compiled directly to native code at build time. No runtime overhead.
- **Rust**: same as C — no runtime, no JIT, binary is native from the start.
- **C#**: warm-up cost on first run (JIT), but the JIT can apply runtime-specific optimizations (e.g., inlining based on actual CPU, profile-guided decisions). Long-running processes often reach near-native speed after warm-up.

**.NET vs Mono**: there are two major CLR implementations:
- **.NET 6/7/8** (formerly .NET Core): Microsoft's modern, cross-platform runtime. Preferred for new projects.
- **Mono**: older, community-maintained runtime. Used by Unity and older Linux tooling (what we used in this session).

---

## 2. Memory Management — GC vs Ownership vs malloc

This is the biggest conceptual shift coming from Rust or C.

C# uses a **garbage collector** (GC) — you allocate, and the runtime frees memory automatically when objects are no longer reachable.

| | C | Rust | C# |
|---|---|---|---|
| Allocation | `malloc` / `free` | stack by default, `Box<T>` for heap | `new` for heap (classes), stack for structs |
| Deallocation | manual `free` | automatic via ownership/drop | automatic via GC |
| Dangling pointers | possible | impossible (compile-time) | impossible (GC keeps object alive) |
| Use-after-free | possible | impossible | impossible |
| Double free | possible | impossible | impossible |
| GC pauses | none | none | yes — short but non-deterministic |

**GC pauses** are the main runtime cost. For most applications (web APIs, desktop apps, games) they are imperceptible. For hard real-time systems (e.g., embedded, trading systems with microsecond latency), they are a problem.

C# gives you escape hatches:
- `stackalloc` — allocate on the stack, like a C VLA or Rust stack array
- `Span<T>` / `Memory<T>` — zero-copy slices over stack/heap/unmanaged memory (like Rust slices `&[T]`)
- `unsafe` blocks with raw pointers — exactly like C
- `NativeMemory.Alloc` — manual `malloc`/`free` equivalent when you need it

### IDisposable — Deterministic Cleanup

For resources that must be released immediately (file handles, sockets, DB connections), C# has `IDisposable` and the `using` statement — analogous to Rust's `Drop` trait:

```csharp
// Rust Drop equivalent
using (var file = File.OpenRead("data.txt"))
{
    // file is closed/freed here automatically, not waiting for GC
}

// Modern shorthand (C# 8+)
using var file = File.OpenRead("data.txt"); // disposed at end of scope
```

---

## 3. Type System

### Value Types vs Reference Types

C# splits types into two categories, similar to Rust's stack vs heap distinction:

| | Value Type | Reference Type |
|---|---|---|
| Storage | stack (usually) | heap |
| Assignment | copies the value | copies the reference (pointer) |
| C analogy | `int`, `struct` on stack | `malloc`'d struct, passed by pointer |
| Rust analogy | `i32`, struct by value | `Box<T>`, `Rc<T>` |
| C# examples | `int`, `float`, `bool`, `struct`, `enum` | `class`, `string`, arrays, delegates |

```csharp
// Value type — copy semantics (like C struct assignment)
struct Point { public int X, Y; }
Point a = new Point { X = 1, Y = 2 };
Point b = a;   // full copy
b.X = 99;      // a.X is still 1

// Reference type — pointer semantics (like C pointer assignment)
class Node { public int Value; }
Node a = new Node { Value = 1 };
Node b = a;    // b points to the same object
b.Value = 99;  // a.Value is now 99
```

### Nullable Types

By default, value types cannot be null. Reference types can (a footgun C# 8 started fixing):

```csharp
int x = null;       // compile error — int is a value type
int? x = null;      // OK — Nullable<int>, like Rust's Option<i32>

string s = null;    // allowed pre-C#8, dangerous
string? s = null;   // C# 8+ explicit nullable reference type
string s = null;    // C# 8+ with nullable enabled: compiler warning
```

This is C#'s answer to Rust's `Option<T>`. It's opt-in at the project level and less rigorous than Rust's type system, but it catches many null dereference bugs at compile time when enabled.

---

## 4. Classes and Structs

### Classes (Reference Types)

The primary OOP building block. Supports inheritance, interfaces, virtual dispatch.

```csharp
class Animal
{
    public string Name { get; set; }      // property (not a raw field)
    private int _age;                      // private field

    public Animal(string name, int age)   // constructor
    {
        Name = name;
        _age = age;
    }

    public virtual string Speak() => "...";   // virtual = can be overridden
}

class Dog : Animal                            // single inheritance only
{
    public Dog(string name) : base(name, 0) {}
    public override string Speak() => "Woof";
}
```

C has no classes. In Rust, the closest equivalent is `impl` blocks + `trait` objects, but without inheritance.

### Structs (Value Types)

Like C structs — live on the stack, copy by value. In C# they can also have methods and implement interfaces.

```csharp
struct Vector3
{
    public float X, Y, Z;

    public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public float Length() => MathF.Sqrt(X*X + Y*Y + Z*Z);
}
```

Like Rust structs — no inheritance, but can implement interfaces (like Rust traits). Prefer structs for small, short-lived data (math types, coordinates) to avoid GC pressure.

### Records (C# 9+)

Immutable data types with value-based equality — the closest thing to Rust's `#[derive(PartialEq, Clone)]` structs:

```csharp
record Point(int X, int Y);  // immutable, equality by value, auto-generates ToString

var a = new Point(1, 2);
var b = new Point(1, 2);
Console.WriteLine(a == b);   // true — compares values, not references

// "Mutation" creates a new copy, like Rust's struct update syntax
var c = a with { X = 99 };   // c = Point(99, 2), a is unchanged
```

---

## 5. Interfaces and Generics

### Interfaces — Like Rust Traits

Interfaces define a contract (a set of methods a type must implement). Unlike Rust traits, they use runtime virtual dispatch by default (vtable), not monomorphization.

```csharp
interface IShape
{
    double Area();
    double Perimeter();
}

class Circle : IShape
{
    double _r;
    public Circle(double r) { _r = r; }
    public double Area() => Math.PI * _r * _r;
    public double Perimeter() => 2 * Math.PI * _r;
}
```

In Rust: `trait IShape { fn area(&self) -> f64; }` + `impl IShape for Circle`.

A key difference: in C#, a type must explicitly declare it implements an interface (`class Circle : IShape`). In Rust, trait implementations are structural — any type implementing the required methods satisfies the trait.

### Generics

Similar to Rust generics and C++ templates, but resolved at compile time (monomorphized for value types, shared for reference types):

```csharp
// Generic class
class Stack<T>
{
    private List<T> _items = new();
    public void Push(T item) => _items.Add(item);
    public T Pop() { var last = _items[^1]; _items.RemoveAt(_items.Count - 1); return last; }
}

// Generic method with constraint (like Rust trait bounds)
T Max<T>(T a, T b) where T : IComparable<T>
    => a.CompareTo(b) >= 0 ? a : b;
```

`where T : IComparable<T>` is equivalent to Rust's `T: PartialOrd`. C# constraints can require:
- Interface implementation: `where T : IDisposable`
- Class or struct: `where T : class` / `where T : struct`
- Constructor: `where T : new()`

---

## 6. Error Handling — Exceptions vs Result

C# uses **exceptions** for error handling, not return values like Rust's `Result<T, E>`.

```csharp
// C# exception style
try
{
    var text = File.ReadAllText("missing.txt");  // throws FileNotFoundException
    int n = int.Parse("abc");                    // throws FormatException
}
catch (FileNotFoundException ex)
{
    Console.WriteLine($"File not found: {ex.Message}");
}
catch (Exception ex)          // catch-all, like Rust's _ => pattern
{
    Console.WriteLine($"Error: {ex.Message}");
}
finally                       // always runs, like Rust's Drop or defer in Go
{
    // cleanup
}
```

Compared to Rust's `Result<T, E>`:
- Exceptions are invisible in the type signature — a function that throws gives no compile-time indication (unlike `Result`)
- No compiler forcing you to handle errors (unlike Rust's `#[must_use]`)
- But the pattern is simpler for rapid development

Some C# libraries provide `Result`-like patterns, and C# does have `TryXxx` patterns as a convention:

```csharp
// TryParse pattern — no exception, returns bool, result via out parameter
if (int.TryParse("123", out int n))
    Console.WriteLine(n);  // 123
```

---

## 7. Pattern Matching

C# has grown powerful pattern matching, now similar in expressiveness to Rust's `match`:

```csharp
// Switch expression (C# 8+) — like Rust's match
string Classify(object obj) => obj switch
{
    int n when n < 0    => "negative int",
    int n               => $"positive int: {n}",
    string s            => $"string of length {s.Length}",
    null                => "null",
    _                   => "something else"       // _ is the wildcard, like Rust
};

// Property patterns
string DescribePoint(Point p) => p switch
{
    { X: 0, Y: 0 }  => "origin",
    { X: 0 }        => "on Y axis",
    { Y: 0 }        => "on X axis",
    _               => "somewhere else"
};
```

One gap vs Rust: C# enums don't carry data (they're just named integers, like C enums). There's no native equivalent to Rust's `enum Result<T, E> { Ok(T), Err(E) }`. You simulate discriminated unions with class hierarchies or third-party libraries.

---

## 8. Enums

C# enums are essentially named integer constants, exactly like C:

```csharp
enum Direction { North, South, East, West }  // North=0, South=1, ...
enum Flags { Read = 1, Write = 2, Execute = 4 }  // bit flags

Direction d = Direction.North;
```

Unlike Rust enums, they **cannot carry data**. `Option<T>` and `Result<T,E>` don't exist natively — use nullable types or class hierarchies instead.

---

## 9. Unsafe Code and Pointers

C# has an `unsafe` keyword that enables raw pointer arithmetic, exactly like C:

```csharp
unsafe void Swap(int* a, int* b)
{
    int tmp = *a;
    *a = *b;
    *b = tmp;
}

unsafe void Example()
{
    int x = 1, y = 2;
    Swap(&x, &y);  // x=2, y=1
}
```

Also available: `fixed` to pin GC objects so the GC doesn't move them while you hold a pointer:

```csharp
unsafe void ProcessBuffer(byte[] buffer)
{
    fixed (byte* ptr = buffer)  // pins buffer in memory
    {
        // ptr is a raw C-style pointer valid within this block
        for (int i = 0; i < buffer.Length; i++)
            ptr[i] ^= 0xFF;
    }
}
```

`stackalloc` allocates on the stack (no GC involvement):

```csharp
Span<int> buffer = stackalloc int[64];  // like int buf[64] in C
```

---

## 10. Async/Await

C# invented the `async`/`await` pattern that Rust (and others) later adopted. The concept is identical to Rust's:

```csharp
// C#
async Task<string> FetchDataAsync(string url)
{
    using var client = new HttpClient();
    return await client.GetStringAsync(url);  // non-blocking wait
}

// Call it
string data = await FetchDataAsync("https://example.com");
```

```rust
// Rust equivalent
async fn fetch_data(url: &str) -> Result<String, reqwest::Error> {
    reqwest::get(url).await?.text().await
}
```

Key differences from Rust:
- C# async uses a thread pool runtime built into the CLR — no need to choose an executor like Tokio
- `Task<T>` is C#'s equivalent of Rust's `Future<Output = T>`
- `Task` (no generic) = `Future<Output = ()>`
- C# async methods start executing immediately up to the first `await`; Rust futures are lazy (nothing runs until polled)

---

## 11. LINQ — Functional Data Processing

LINQ (Language Integrated Query) is one of C#'s most distinctive features — a set of extension methods for querying and transforming collections, similar to Rust's iterator combinators:

```csharp
var numbers = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

// LINQ method syntax
var result = numbers
    .Where(n => n % 2 == 0)     // filter — like Rust's .filter()
    .Select(n => n * n)          // map — like Rust's .map()
    .Take(3)                     // like Rust's .take()
    .ToList();                   // collect — like Rust's .collect()
// result = [4, 16, 36]

// LINQ query syntax (SQL-like — compiles to the same thing)
var result2 = (from n in numbers
               where n % 2 == 0
               select n * n).Take(3).ToList();
```

Rust equivalents side by side:

| C# LINQ | Rust Iterator |
|---|---|
| `.Where(x => ...)` | `.filter(\|x\| ...)` |
| `.Select(x => ...)` | `.map(\|x\| ...)` |
| `.SelectMany(x => ...)` | `.flat_map(\|x\| ...)` |
| `.Aggregate(seed, (acc, x) => ...)` | `.fold(seed, \|acc, x\| ...)` |
| `.Any(x => ...)` | `.any(\|x\| ...)` |
| `.All(x => ...)` | `.all(\|x\| ...)` |
| `.FirstOrDefault(x => ...)` | `.find(\|x\| ...)` |
| `.OrderBy(x => ...)` | `.sorted_by_key(\|x\| ...)` |
| `.ToList()` | `.collect::<Vec<_>>()` |
| `.ToDictionary(k, v)` | `.collect::<HashMap<_,_>>()` |

---

## 12. Delegates and Lambdas

A delegate is a typed function pointer — like a `fn` pointer in C or a `fn()` type in Rust, but also usable as a multicast (multiple subscribers):

```csharp
// Built-in delegate types (no need to define your own usually)
Func<int, int, int> add = (a, b) => a + b;   // like Rust's Fn(i32, i32) -> i32
Action<string> print = s => Console.WriteLine(s);  // returns void, like Fn(&str)
Predicate<int> isEven = n => n % 2 == 0;     // returns bool

// Events — multicast delegates (publisher/subscriber pattern)
class Button
{
    public event Action<string> Clicked;  // event = multicast delegate
    public void Click() => Clicked?.Invoke("button1");
}

var btn = new Button();
btn.Clicked += name => Console.WriteLine($"{name} clicked");
btn.Clicked += name => Console.WriteLine($"Logging: {name}");
btn.Click();  // both handlers run
```

---

## 13. Properties

C# properties are syntactic sugar over getter/setter methods, inspired by the observation that public fields are an encapsulation risk:

```csharp
class Person
{
    private int _age;

    // Full property
    public int Age
    {
        get => _age;
        set
        {
            if (value < 0) throw new ArgumentException("Age cannot be negative");
            _age = value;
        }
    }

    // Auto-property (compiler generates the backing field)
    public string Name { get; set; }

    // Read-only auto-property
    public DateTime CreatedAt { get; } = DateTime.Now;
}

var p = new Person();
p.Name = "Alice";   // looks like field access, calls the setter
p.Age = -1;         // throws ArgumentException
```

No direct equivalent in C or Rust — in C you'd use explicit `get_age()`/`set_age()` functions; in Rust you'd use methods.

---

## 14. Extension Methods

Add methods to existing types without subclassing — like Rust's `impl` blocks for types you don't own:

```csharp
static class StringExtensions
{
    public static bool IsPalindrome(this string s)
    {
        var clean = s.ToLower();
        return clean == new string(clean.Reverse().ToArray());
    }
}

// Now usable as if it were a method on string:
"racecar".IsPalindrome();  // true
"hello".IsPalindrome();    // false
```

In Rust, you can `impl` a trait for a foreign type (with orphan rule restrictions). C# extension methods are similar but without the trait requirement.

---

## 15. Performance Characteristics

| Scenario | C | Rust | C# |
|---|---|---|---|
| CPU-bound computation | fastest | fastest | ~1.2–2x slower (JIT + GC overhead) |
| Memory allocation throughput | fast (malloc) | fast (stack/allocator) | fast but GC pauses |
| Startup time | instant | instant | slow (JIT warm-up), faster with NativeAOT |
| Long-running server | N/A | excellent | excellent (JIT optimizes over time) |
| Hard real-time | yes | yes | not suitable (GC pauses) |
| Unsafe numeric/SIMD code | excellent | excellent | excellent (with `unsafe` + `System.Numerics`) |

**NativeAOT** (.NET 8+): compiles C# to a self-contained native binary with no JIT and no CLR, giving startup times and binary sizes comparable to C. Trades runtime JIT optimizations for cold-start performance.

---

## 16. Best Use Cases

| Use Case | Verdict |
|---|---|
| Web backends / APIs | Excellent — ASP.NET Core is very fast |
| Desktop GUI (Windows) | Excellent — WinForms, WPF, MAUI |
| Desktop GUI (Linux) | Good — GTK# or MAUI (newer) |
| Game development | Excellent — Unity uses C# as its scripting language |
| Enterprise / business logic | Excellent — the dominant language in this space |
| CLI tooling | Good |
| Systems programming | Limited — use C or Rust; C# `unsafe` exists but is not ergonomic for this |
| Embedded / real-time | Not suitable — GC pauses, large runtime |
| High-frequency trading | Possible with careful GC tuning, but Rust/C preferred |

---

## 17. Key Limitations

1. **GC non-determinism** — you cannot predict exactly when memory is freed.
2. **No ownership model** — no compile-time aliasing or lifetime guarantees like Rust.
3. **Exceptions are invisible** — function signatures don't tell you what can throw.
4. **Enum limitations** — no data-carrying enum variants (unlike Rust).
5. **Single inheritance** — classes can only inherit from one base class (interfaces are unlimited).
6. **Runtime dependency** — requires .NET/Mono installed unless you use NativeAOT.
7. **Startup latency** — JIT warm-up makes short-lived CLI tools slower than native equivalents.

---

## 18. Deep Dive — Delegates and Lambdas

### The mental model

A **delegate** is a type whose values are callable functions. Think of it as a named function-pointer type:

```c
// C
typedef int (*BinaryOp)(int, int);   // a type for "any function taking two ints, returning int"
BinaryOp op = &add;
int result = op(3, 4);               // call through the pointer
```

```csharp
// C# equivalent
delegate int BinaryOp(int a, int b); // declare the delegate type
BinaryOp op = Add;                   // assign a method
int result = op(3, 4);               // call it
```

The difference from a raw C function pointer: a delegate is a full object. It can capture a `this` reference (bound to an instance method), and it supports **multicasting** (multiple functions chained together).

---

### Built-in generic delegate types

Defining a named delegate type every time is verbose. C# provides three generic families that cover almost everything:

| Type | Signature | Rust equivalent |
|---|---|---|
| `Action` | `() → void` | `fn()` |
| `Action<T>` | `(T) → void` | `fn(T)` |
| `Action<T1,T2>` | `(T1,T2) → void` | `fn(T1,T2)` |
| `Func<TResult>` | `() → TResult` | `fn() -> R` |
| `Func<T,TResult>` | `(T) → TResult` | `fn(T) -> R` |
| `Func<T1,T2,TResult>` | `(T1,T2) → TResult` | `fn(T1,T2) -> R` |
| `Predicate<T>` | `(T) → bool` | `fn(T) -> bool` |

```csharp
Func<int, int, int>  add   = (a, b) => a + b;
Action<string>       print = s => Console.WriteLine(s);
Predicate<int>       even  = n => n % 2 == 0;

add(3, 4);      // 7
print("hi");    // prints "hi"
even(6);        // true
```

---

### Lambda syntax

A lambda is just an anonymous function written inline. Two forms:

```csharp
// Expression lambda — single expression, value is the implicit return
Func<int, int> square = x => x * x;

// Statement lambda — block body, explicit return
Func<int, int> abs = x =>
{
    if (x < 0) return -x;
    return x;
};
```

When the compiler can infer types, you can omit them. When there are multiple parameters or none:

```csharp
Action greet = () => Console.WriteLine("hello");     // no parameters
Func<int, int, int> add = (a, b) => a + b;           // two parameters, types inferred
Func<int, int, int> add2 = (int a, int b) => a + b;  // explicit types
```

---

### Closures — capturing variables

Lambdas can capture variables from the enclosing scope. The captured variable is shared by reference — the lambda sees the current value of the variable, not a copy at the time it was created.

```csharp
int count = 0;
Action increment = () => count++;  // captures 'count' by reference

increment();
increment();
Console.WriteLine(count);  // 2 — the lambda mutated the original variable
```

**The classic loop-capture gotcha:**

```csharp
// Wrong — all lambdas capture the same 'i' variable
var actions = new List<Action>();
for (int i = 0; i < 3; i++)
    actions.Add(() => Console.WriteLine(i));

actions[0]();  // prints 3, not 0
actions[1]();  // prints 3, not 1
actions[2]();  // prints 3, not 2
// 'i' is 3 after the loop ends; all lambdas share it

// Correct — capture a local copy
for (int i = 0; i < 3; i++)
{
    int copy = i;
    actions.Add(() => Console.WriteLine(copy));  // each lambda captures its own 'copy'
}
```

In Rust this cannot happen — closures capture by value or by `move`, and the borrow checker prevents dangling captures. In C, function pointers cannot capture variables at all (no closures); you pass a `void*` context pointer manually.

---

### Multicast delegates — multiple subscribers

A delegate instance can hold more than one function. `+=` adds a function; `-=` removes it. When you invoke the delegate, all registered functions run in order.

```csharp
Action<string> log = s => Console.WriteLine($"[Console] {s}");
log += s => File.AppendAllText("log.txt", s + "\n");   // add a second handler
log += s => SendToRemoteServer(s);                      // add a third

log("Error occurred");
// All three run in registration order
```

This is the foundation of C# **events** — an event is a delegate field with restricted access: outside the class, you can only `+=` and `-=`, not assign or invoke directly.

```csharp
class Button
{
    // 'event' keyword restricts external access
    public event Action<string> Clicked;

    public void SimulateClick() => Clicked?.Invoke("btn1");
    //                                     ^ null-conditional: only calls if non-null
}

var btn = new Button();
btn.Clicked += name => Console.WriteLine($"Handler 1: {name}");
btn.Clicked += name => Console.WriteLine($"Handler 2: {name}");
btn.SimulateClick();
// Handler 1: btn1
// Handler 2: btn1
```

`Clicked?.Invoke(...)` is the null-conditional operator: if `Clicked` is null (no subscribers), the call is skipped silently. Without it you'd get a `NullReferenceException`.

---

### `ref` parameters in delegates (engine-specific)

Standard `Action<T>` and `Func<T,R>` cannot express `ref` parameters. The engine defines custom delegate types for `World.ForEach` precisely because of this:

```csharp
delegate void EcsAction<T>(Entity e, ref T component) where T : struct;

// Usage — component is mutated directly in the sparse set, no copy
world.ForEach<Transform>((Entity e, ref Transform t) =>
{
    t.Position.X += 1f;  // modifies the stored value
});
```

If `ref` were not used, `t` would be a copy — modifying it would do nothing to the stored component.

---

## 19. Deep Dive — Enums With Data (Discriminated Unions)

### C# enums vs Rust enums

C# enums are **named integers**, identical to C enums:

```csharp
enum Direction { North, South, East, West }  // North=0, South=1, East=2, West=3
Direction d = Direction.North;
```

They cannot carry data. `Direction.North` is always just the integer `0`.

Rust enums are **algebraic data types** (sum types / tagged unions). Each variant can carry different data:

```rust
enum Shape {
    Circle(f32),            // carries a radius
    Rectangle(f32, f32),   // carries width and height
    Point,                  // carries nothing
}
```

C has no built-in equivalent, but you can simulate it manually:

```c
// C — tagged union
typedef struct {
    enum { CIRCLE, RECTANGLE, POINT } tag;
    union {
        float radius;
        struct { float w, h; } rect;
    } data;
} Shape;
```

---

### How to simulate data-carrying enums in C#

**Option 1 — Abstract class hierarchy (classic OOP)**

```csharp
abstract class Shape { }

sealed class Circle    : Shape { public float Radius; }
sealed class Rectangle : Shape { public float Width, Height; }
sealed class Point     : Shape { }
```

Usage with pattern matching:

```csharp
void Describe(Shape s)
{
    switch (s)
    {
        case Circle c:
            Console.WriteLine($"Circle, radius {c.Radius}");
            break;
        case Rectangle r:
            Console.WriteLine($"Rect {r.Width}×{r.Height}");
            break;
        case Point:
            Console.WriteLine("Point");
            break;
    }
}
```

**Option 2 — Abstract records (modern, C# 9+)**

Records auto-generate constructors, equality, and `ToString`. Combined with `abstract`, they give the cleanest simulation of Rust enums:

```csharp
abstract record Shape;
record Circle(float Radius)         : Shape;
record Rectangle(float W, float H)  : Shape;
record Point                        : Shape;
```

Usage with switch expression (identical logic, less syntax):

```csharp
string Describe(Shape s) => s switch
{
    Circle c      => $"Circle, radius {c.Radius}",
    Rectangle r   => $"Rect {r.W}×{r.H}",
    Point         => "Point",
    _             => throw new ArgumentOutOfRangeException()
};
```

Side by side with Rust:

```rust
// Rust
match shape {
    Shape::Circle(r)      => println!("Circle, radius {}", r),
    Shape::Rectangle(w,h) => println!("Rect {}x{}", w, h),
    Shape::Point          => println!("Point"),
}
```

```csharp
// C# with records
string result = shape switch
{
    Circle c      => $"Circle, radius {c.Radius}",
    Rectangle r   => $"Rect {r.W}×{r.H}",
    Point         => "Point",
    _             => "unknown"
};
```

The key difference: Rust's compiler enforces **exhaustiveness** — if you forget a variant, it's a compile error. C#'s `switch` expression also exhausts at compile time if there is no `_` wildcard, but only when it can statically prove all cases are handled (it's less reliable than Rust's).

**Option 3 — `Result<T,E>` from a library**

The NuGet package `OneOf` provides a fluent discriminated union:

```csharp
OneOf<Success, NotFound, Error> result = DoSomething();
result.Switch(
    success => Console.WriteLine("OK"),
    notFound => Console.WriteLine("404"),
    error => Console.WriteLine(error.Message)
);
```

---

### `Option<T>` — nullable reference types as a substitute

Rust's `Option<T>` maps to:
- `T?` where `T` is a value type → `Nullable<T>` (e.g. `int?`)
- `T?` where `T` is a reference type → nullable reference type annotation (C# 8+)

```csharp
int? maybeInt = null;
if (maybeInt.HasValue)
    Console.WriteLine(maybeInt.Value);

// Equivalent to Rust's if let Some(n) = maybe_int
if (maybeInt is int n)
    Console.WriteLine(n);
```

It's less safe than Rust's `Option` — C# doesn't prevent you from ignoring the null case at compile time, though analyzers will warn.

---

## 20. Deep Dive — Async/Await

### The problem async solves

Suppose you fetch data from a server. The naive approach blocks the calling thread for the entire duration of the network wait:

```csharp
// Synchronous — thread is blocked, doing nothing, for the whole request
string data = DownloadString("https://example.com");  // may take 200ms
Console.WriteLine(data);
```

If this is the UI thread, the window freezes for 200ms. If this is a server handling 10,000 requests, you'd need 10,000 threads just sitting idle — expensive.

**Async I/O** lets the thread be released back to the pool while waiting, and the continuation runs when the data arrives. One thread can service thousands of concurrent operations.

---

### `Task<T>` — C#'s future type

`Task<T>` represents a computation that will produce a `T` at some point. It is C#'s equivalent of Rust's `Future<Output = T>` or JavaScript's `Promise<T>`.

| C# | Rust |
|---|---|
| `Task<T>` | `impl Future<Output = T>` |
| `Task` | `impl Future<Output = ()>` |
| `async` method | `async fn` |
| `await expr` | `expr.await` |
| `Task.WhenAll` | `join!` (Tokio) |

**Key difference from Rust:** `Task` is **eager** — it starts running immediately when created. Rust's futures are **lazy** — nothing happens until they are polled by an executor.

---

### How `async`/`await` works

The compiler transforms an `async` method into a **state machine**. Each `await` is a suspension point. When the awaited operation completes, execution resumes from that exact point.

```csharp
async Task<string> FetchAndProcessAsync(string url)
{
    // Thread is released here while waiting for the network
    string raw = await httpClient.GetStringAsync(url);

    // Thread resumes here (possibly a different thread from the pool)
    return raw.ToUpper();
}
```

The compiler rewrites this into roughly:

```csharp
// (Conceptual — actual generated code is more complex)
Task<string> FetchAndProcessAsync(string url)
{
    var tcs = new TaskCompletionSource<string>();
    var networkTask = httpClient.GetStringAsync(url);
    networkTask.ContinueWith(t =>
    {
        string raw = t.Result;
        tcs.SetResult(raw.ToUpper());
    });
    return tcs.Task;
}
```

You never write the continuation manually — `await` does it for you. The original thread is not blocked; it returns to the thread pool immediately.

---

### Calling async methods

`await` can only be used inside an `async` method. The `async` annotation propagates up the call stack — this is the "async contagion" you'll hear about.

```csharp
// An async method
async Task<int> GetCountAsync()
{
    var data = await FetchDataAsync();
    return data.Length;
}

// Awaiting it from another async method
async Task RunAsync()
{
    int count = await GetCountAsync();
    Console.WriteLine(count);
}

// Top-level entry point (C# 7.1+) — Main can be async
static async Task Main()
{
    await RunAsync();
}
```

---

### Running multiple tasks concurrently

`await` in sequence means one-at-a-time. To run concurrently, start all tasks first, then await them:

```csharp
// Sequential — waits for each before starting the next
string a = await FetchAsync("url1");   // 200ms
string b = await FetchAsync("url2");   // 200ms more = 400ms total

// Concurrent — both run in parallel
Task<string> taskA = FetchAsync("url1");  // starts immediately
Task<string> taskB = FetchAsync("url2");  // starts immediately
string a = await taskA;                   // ~200ms total
string b = await taskB;                   // already done

// Or with WhenAll
string[] results = await Task.WhenAll(FetchAsync("url1"), FetchAsync("url2"));
```

---

### `ConfigureAwait(false)`

By default, after an `await`, execution resumes on the **original synchronization context** (e.g. the UI thread). In library code that doesn't need to touch UI, this is unnecessary overhead. `ConfigureAwait(false)` says "resume on any thread pool thread":

```csharp
// Library code — doesn't care which thread resumes it
async Task<string> LibraryMethodAsync()
{
    var data = await httpClient.GetStringAsync(url).ConfigureAwait(false);
    return Process(data);  // runs on a thread pool thread, not UI thread
}
```

Game/application code that updates UI state should not use `ConfigureAwait(false)` because it may then try to update UI from a non-UI thread.

---

### `async void` — avoid it

`async void` cannot be awaited. Exceptions thrown inside it are unobserved and will crash the process. The only legitimate use is event handlers (which must match `void` signatures):

```csharp
// OK — event handler, void is required by the event signature
button.Clicked += async (s, e) =>
{
    await DoSomethingAsync();
};

// Bad — exception is silently swallowed or crashes the process
async void BadFire() { await SomethingAsync(); }
```

Always return `Task` or `Task<T>` from async methods, not `void`.

---

## 21. Deep Dive — Error Handling

### The three models

| Language | Mechanism | Compiler enforcement |
|---|---|---|
| C | Return codes + `errno` | None — you can silently ignore |
| Rust | `Result<T,E>` + `?` operator | `#[must_use]` — ignoring is a warning/error |
| C# | Exceptions | None — you can let them propagate silently |

---

### The C approach (for context)

```c
FILE* f = fopen("data.txt", "r");
if (f == NULL) {
    perror("fopen failed");
    return -1;
}
// must manually check every call
```

Errors are values in the return type. No automatic propagation — you write every check yourself.

---

### The Rust approach (for contrast)

```rust
fn read_file() -> Result<String, io::Error> {
    let content = fs::read_to_string("data.txt")?;  // ? propagates the error up
    Ok(content)
}
```

`Result<T,E>` forces the caller to handle both cases. `?` is syntactic sugar for "if Err, return it; if Ok, unwrap the value." The compiler warns if you ignore a `Result`.

---

### C# exceptions — how they work

An exception is thrown anywhere in the call stack and unwinds upward until caught. If nothing catches it, the process terminates.

```csharp
void ReadFile(string path)
{
    // These can throw — no indication in the signature
    string text = File.ReadAllText(path);   // throws FileNotFoundException
    int n = int.Parse(text);               // throws FormatException
    Console.WriteLine(n * 2);
}

// Catching exceptions
try
{
    ReadFile("data.txt");
}
catch (FileNotFoundException ex)
{
    Console.WriteLine($"File missing: {ex.FileName}");
}
catch (FormatException ex)
{
    Console.WriteLine($"Bad format: {ex.Message}");
}
catch (Exception ex)       // catch-all — matches anything derived from Exception
{
    Console.WriteLine($"Unexpected: {ex}");
}
finally
{
    // Always runs, even if an exception was thrown and not caught
    // Even if a caught block re-throws
    Console.WriteLine("Cleanup");
}
```

`finally` is equivalent to Rust's `Drop` or C's `goto cleanup` label — it guarantees cleanup runs regardless of the code path taken.

---

### The exception hierarchy

All exceptions derive from `System.Exception`. The hierarchy is designed so you can catch specific types or broad categories:

```
Exception
├── SystemException
│   ├── NullReferenceException     — accessed a null reference
│   ├── IndexOutOfRangeException   — array index out of bounds
│   ├── InvalidCastException       — bad (Type) cast
│   ├── InvalidOperationException  — called method in invalid state
│   ├── ArgumentException
│   │   ├── ArgumentNullException  — null passed where not allowed
│   │   └── ArgumentOutOfRangeException
│   └── ArithmeticException
│       └── DivideByZeroException
└── IOException
    ├── FileNotFoundException
    ├── DirectoryNotFoundException
    └── EndOfStreamException
```

Catch the **most specific** type first. If you put `catch (Exception)` before `catch (FileNotFoundException)`, the specific handler is never reached — the compiler warns about this.

---

### Throwing exceptions

```csharp
void SetAge(int age)
{
    if (age < 0)
        throw new ArgumentOutOfRangeException(nameof(age), "Age cannot be negative");
    _age = age;
}

// Re-throwing — preserves the original stack trace
catch (IOException ex)
{
    Log(ex);
    throw;         // re-throws the caught exception — DO NOT use 'throw ex' (resets stack trace)
}

// Wrapping — adds context
catch (IOException ex)
{
    throw new ApplicationException("Failed to load config", ex);  // ex is the inner exception
}
```

---

### Custom exceptions

```csharp
// Convention: name ends in "Exception", inherit from Exception or a subclass
public class GameStateException : InvalidOperationException
{
    public GameStateException(string message) : base(message) { }
    public GameStateException(string message, Exception inner) : base(message, inner) { }
}

// Throw it
throw new GameStateException("Cannot push state: stack is full");
```

---

### When NOT to use exceptions

Exceptions carry overhead (stack unwinding, object allocation). For **expected failure conditions** on hot paths, use the `TryXxx` pattern instead — a `bool` return with an `out` parameter:

```csharp
// Bad for performance — exception thrown on every invalid parse
try { int n = int.Parse(userInput); }
catch (FormatException) { /* handle */ }

// Good — no exception, just a bool
if (int.TryParse(userInput, out int n))
    Console.WriteLine($"Parsed: {n}");
else
    Console.WriteLine("Invalid number");
```

The same pattern appears throughout the BCL: `Dictionary.TryGetValue`, `File.Exists` (check before open), `int.TryParse`, `Guid.TryParse`, etc.

As a rule of thumb: use exceptions for **exceptional, unexpected failures** (disk full, network down, programmer error). Use `TryXxx` / return values for **expected alternative outcomes** (user typed a bad number, key not in dictionary).

---

### Comparison table — error propagation

```csharp
// C# — exceptions propagate automatically, no boilerplate
string ProcessFile(string path)
{
    string text = File.ReadAllText(path);   // throws if missing, caller handles it
    return text.ToUpper();
}
```

```rust
// Rust — explicit propagation with '?'
fn process_file(path: &str) -> Result<String, io::Error> {
    let text = fs::read_to_string(path)?;  // returns Err if missing
    Ok(text.to_uppercase())
}
```

```c
// C — manual propagation, every call site
int process_file(const char* path, char* out, size_t out_len) {
    FILE* f = fopen(path, "r");
    if (!f) return -1;
    // ... read, check again, close, etc.
    return 0;
}
```

C# exceptions give you the least boilerplate but the least visibility — a function's signature gives no hint about what it can throw. Rust's `Result` gives you the most safety (enforced by the compiler) with moderate boilerplate (the `?` operator). C gives you full control and maximum boilerplate.
