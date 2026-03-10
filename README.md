# Tailbox CVA

High-performance, zero-allocation variant management for s&box UI, inspired by `class-variance-authority` (CVA).

[![s&box Package](https://img.shields.io/badge/s&box-Package-blue.svg)](https://sbox.game/agelaste/tailbox_cva)
[![GitHub Project](https://img.shields.io/badge/GitHub-Project-lightgrey?logo=github)](https://github.com/Jeremy84100/tailbox_cva)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

## About

Tailbox CVA is a professional-grade C# utility designed for managing component styles and variants within the s&box environment. It allows developers to define a clean design system for their components, handling different states (sizes, variants, statuses) without messy string concatenation or complex logic.

Built with a **"Performance-First"** mindset, Tailbox CVA is optimized for the high-frequency rendering requirements of a game engine, reaching near-theoretical limits for style resolution speeds.

## Features

- **Type-Safe Design System**: Define your component's look and feel with clear categories and cases.
- **Dynamic Bit-Packing**: Compresses component states into a single `ulong` hash for $O(1)$ lookups.
- **Thread-Static Composition**: Eliminates runtime allocations by reusing thread-local buffers.
- **Fluent Resolver API**: Optimized `ref struct` resolver for zero-allocation property assignment.
- **Compound Variants**: Apply specific styles only when multiple conditions are met. Supports automatic boolean variant generation.
- **Permutation Caching**: Blazing fast result retrieval using a thread-safe `ConcurrentDictionary` cache.

## Usage

### 1. Defining a CVA

```csharp
using Tailbox;

public static class ButtonStyles
{
    // Implicit operator supports .Bake() automatically
    public static readonly TailboxCva Styles = TailboxCva.Create("inline-flex items-center justify-center rounded-md")
        .Variant("variant", v => v
            .Case("default", "bg-primary text-primary-foreground", isDefault: true)
            .Case("destructive", "bg-destructive text-destructive-foreground")
            .Case("outline", "border border-input bg-background")
        )
        .Variant("size", s => s
            .Case("default", "h-10 px-4", isDefault: true)
            .Case("sm", "h-9 px-3")
            .Case("lg", "h-11 px-8")
        )
        // Compounds support boolean logic and automatic variant registration
        .Compound("opacity-50 grayscale", ("disabled", true));
}
```

### 2. Applying Styles

#### The Optimized Way (Zero-Allocation)
Using the `Resolve()` API is the most efficient method for hot-path rendering (UI panels).

```csharp
// Returns a CvaResult (struct) - No allocation!
var result = ButtonStyles.Styles.Resolve()
    .With("variant", "destructive")
    .With("size", "sm")
    .With("disabled", IsDisabled)
    .Build("extra-margin-class");

// Use result.BaseClasses and result.ExtraClasses separately 
// or call .ToString() / .Merge()
string finalClasses = result.ToString();
```

#### The Helper Way (Compatibility)
```csharp
string classes = ButtonStyles.Styles.Build("extra-class", ("variant", "outline"), ("size", "lg"));
```

### 3. Setting up Automatic Merging

To enable automatic conflict resolution (requires `TailboxMerge`), set the `MergeHandler` once at app startup:

```csharp
TailboxCva.MergeHandler = (baseStyles, extra) => TailboxMerge.Merge(baseStyles, extra);
```

## Performance Note

Tailbox CVA is built for game engine hot-paths:

- **Zero Runtime Reflection**: Completely eliminates reflection and `TypeLibrary` lookups in favor of pre-calculated integer IDs.
- **Dynamic Bit-Packing**: States are packed into bits based on the number of options (e.g., 2 bits for 4 states). This allows caching thousands of permutations in a single `ulong` key.
- **ThreadStatic Buffers**: Uses `[ThreadStatic]` to avoid GC pressure from `StringBuilder` or intermediate arrays.
- **CvaResult Struct**: Returns style components as a `ref struct` to defer or eliminate final string concatenation.
- **Enum String Cache**: High-performance `ConcurrentDictionary` for zero-allocation enum-to-string conversion.

## License

Tailbox CVA is licensed under the [MIT](LICENSE) license.
Feel free to [contribute on GitHub](https://github.com/Jeremy84100/tailbox_cva) to help improve this library!
