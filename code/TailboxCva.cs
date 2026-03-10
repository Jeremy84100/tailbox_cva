using Sandbox;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace Tailbox
{
    /// <summary>
    /// Represents the result of a CVA resolution, separating static base styles 
    /// from dynamic extra classes to enable zero-allocation merging.
    /// </summary>
    public readonly struct CvaResult : IEquatable<CvaResult>
    {
        /// <summary>The base styles generated from the CVA logic.</summary>
        public readonly string BaseClasses;
        
        /// <summary>The additional classes provided during resolution.</summary>
        public readonly string ExtraClasses;

        public CvaResult( string baseClasses, string extraClasses )
        {
            BaseClasses = baseClasses ?? string.Empty;
            ExtraClasses = extraClasses ?? string.Empty;
        }

        /// <summary>
        /// Merges the base and extra classes using the registered <see cref="TailboxCva.MergeHandler"/>.
        /// Falling back to a standard space-separated join if no handler is present.
        /// </summary>
        public string Merge()
        {
            if ( TailboxCva.MergeHandler != null )
            {
                return TailboxCva.MergeHandler( BaseClasses, ExtraClasses );
            }
            
            if ( string.IsNullOrWhiteSpace( ExtraClasses ) ) return BaseClasses;
            return $"{BaseClasses} {ExtraClasses}";
        }

        public override string ToString() => Merge();

        public bool Equals( CvaResult other )
        {
            return BaseClasses == other.BaseClasses && ExtraClasses == other.ExtraClasses;
        }

        public override bool Equals( object obj )
        {
            return obj is CvaResult other && Equals( other );
        }

        public override int GetHashCode()
        {
            return HashCode.Combine( BaseClasses, ExtraClasses );
        }

        public static bool operator ==( CvaResult left, CvaResult right ) => left.Equals( right );
        public static bool operator !=( CvaResult left, CvaResult right ) => !left.Equals( right );
    }

    /// <summary>
    /// A high-performance, zero-allocation Class Variance Authority (CVA) implementation for s&box.
    /// Uses dynamic bit-packing to generate O(1) permutation hashes and thread-static buffers for composition.
    /// </summary>
    public sealed class TailboxCva
    {
        /// <summary>
        /// Optional delegate to process the final class string (e.g., using TailboxMerge.Merge).
        /// Arguments: (BaseClasses, ExtraClasses)
        /// </summary>
        public static Func<string, string, string> MergeHandler { get; set; }

        private readonly string _baseStyles;
        private readonly VariantData[] _bakedVariants;
        private readonly CompoundVariant[] _bakedCompounds;
        
        private readonly Dictionary<string, int> _variantKeyToId;
        private readonly Dictionary<string, int>[] _variantValueToId;

        // Thread-safe cache for computed style permutations
        private readonly ConcurrentDictionary<ulong, string> _permutationCache = new();

        // Optimized path: Thread-local buffers to track state and compose strings without GC pressure
        [ThreadStatic] private static int[] _tsActiveValueIds;
        [ThreadStatic] private static StringBuilder _tsBuffer;

        private struct VariantData
        {
            public int Id;
            public int DefaultValueId;
            public int BitOffset;
            public int BitMask;
            public Dictionary<int, string> ClassesById; 
        }

        private struct CompoundVariant
        {
            public (int KeyId, int ValueId)[] Requirements;
            public string Classes;
        }

        private TailboxCva(
            string baseStyles, 
            VariantData[] variants, 
            CompoundVariant[] compounds, 
            Dictionary<string, int> keyMap,
            Dictionary<string, int>[] valueMap )
        {
            _baseStyles = baseStyles ?? string.Empty;
            _bakedVariants = variants;
            _bakedCompounds = compounds;
            _variantKeyToId = keyMap;
            _variantValueToId = valueMap;
        }

        /// <summary>Initializes a new CVA builder with optional base styles.</summary>
        public static Builder Create( string baseStyles = "" ) => new Builder( baseStyles );

        /// <summary>Legacy alias for <see cref="Create"/>.</summary>
        public static Builder New( string baseStyles = "" ) => new Builder( baseStyles );

        /// <summary>
        /// Prepares a resolution context for this CVA instance.
        /// Ensure minimal allocations by reusing thread-static buffers.
        /// </summary>
        public CvaResolver Resolve()
        {
            // Elastic reuse of thread-local state buffers
            if ( _tsActiveValueIds == null || _tsActiveValueIds.Length < _bakedVariants.Length )
            {
                _tsActiveValueIds = new int[Math.Max( 16, _bakedVariants.Length )];
            }

            if ( _tsBuffer == null )
            {
                _tsBuffer = new StringBuilder( 512 );
            }
            
            _tsActiveValueIds.AsSpan( 0, _bakedVariants.Length ).Clear();
            return new CvaResolver( this );
        }

        /// <summary>
        /// High-level builder to resolve a specific permutation of styles.
        /// Use <see cref="Resolve"/> for the optimized fluent API.
        /// </summary>
        public string Build( string extraClass, params (string Key, object Value)[] properties )
        {
            var resolver = Resolve();
            foreach ( var prop in properties )
            {
                if ( prop.Value is bool b )
                {
                    resolver.With( prop.Key, b );
                }
                else if ( prop.Value != null )
                {
                    resolver.With( prop.Key, prop.Value.ToString() );
                }
            }
            return resolver.Build( extraClass ).Merge();
        }

        /// <summary>
        /// Configuration builder for creating immutable <see cref="TailboxCva"/> instances.
        /// </summary>
        public class Builder
        {
            private readonly string _baseStyles;
            private readonly List<VariantData> _variants = new();
            private readonly List<(string classes, (string Key, string Value)[] reqs)> _tempCompounds = new();
            
            private readonly Dictionary<string, int> _keyMap = new( StringComparer.OrdinalIgnoreCase );
            private readonly List<Dictionary<string, int>> _valueMap = new();

            private int _currentBitOffset = 0;

            internal Builder( string baseStyles ) => _baseStyles = baseStyles?.Trim();

            /// <summary>Registers a new style variant to the CVA system.</summary>
            public Builder AddVariant( string name, Action<VariantBuilder> buildAction )
            {
                var vb = new VariantBuilder();
                buildAction( vb );

                int keyId = _variants.Count;
                _keyMap[name] = keyId;

                var valueDict = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
                var classesById = new Dictionary<int, string>();
                
                int valueIdCounter = 1; // 0 reserved for 'undefined/default'
                int defaultValueId = 0;

                foreach ( var kvp in vb.Cases )
                {
                    int vId = valueIdCounter++;
                    valueDict[kvp.Key] = vId;
                    classesById[vId] = kvp.Value;

                    if ( vb.DefaultCase != null && kvp.Key.Equals( vb.DefaultCase, StringComparison.OrdinalIgnoreCase ) )
                    {
                        defaultValueId = vId;
                    }
                }

                _valueMap.Add( valueDict );

                // Dynamic bit-packing calculation
                // Determines minimal bit-count required to represent all possible states for this variant
                int bitsNeeded = 0;
                int maxVal = valueIdCounter - 1;
                
                while ( maxVal > 0 )
                {
                    bitsNeeded++;
                    maxVal >>= 1;
                }

                if ( bitsNeeded == 0 ) bitsNeeded = 1;

                if ( _currentBitOffset + bitsNeeded > 64 )
                {
                    Log.Error( $"[Tailbox.CVA] Bit-packing limit exceeded (64 bits). Cannot add variant '{name}'." );
                }

                int bitMask = (1 << bitsNeeded) - 1;

                _variants.Add( new VariantData
                {
                    Id = keyId,
                    DefaultValueId = defaultValueId,
                    BitOffset = _currentBitOffset,
                    BitMask = bitMask,
                    ClassesById = classesById
                } );

                _currentBitOffset += bitsNeeded;

                return this;
            }

            public Builder Variant( string name, Action<VariantBuilder> buildAction ) => AddVariant( name, buildAction );

            /// <summary>Adds a compound rule that applies classes when multiple variant conditions are met.</summary>
            public Builder AddCompound( string classes, params (string Key, string Value)[] requirements )
            {
                _tempCompounds.Add( (classes, requirements) );
                return this;
            }

            /// <summary>Adds a compound rule with automatic support for boolean and object values.</summary>
            public Builder Compound( string classes, params (string Key, object Value)[] requirements )
            {
                var reqs = new (string Key, string Value)[requirements.Length];
                for ( int i = 0; i < requirements.Length; i++ )
                {
                    // Implicitly register boolean variants if they don't exist
                    if ( !_keyMap.ContainsKey( requirements[i].Key ) )
                    {
                        AddVariant( requirements[i].Key, v => v.Case( "true", string.Empty ).Case( "false", string.Empty ) );
                    }

                    string valStr = requirements[i].Value is bool b ? (b ? "true" : "false") : requirements[i].Value?.ToString();
                    reqs[i] = (requirements[i].Key, valStr);
                }
                return AddCompound( classes, reqs );
            }

            /// <summary>Finalizes the configuration and bakes it into a high-performance CVA instance.</summary>
            public TailboxCva Bake()
            {
                var finalCompounds = new List<CompoundVariant>();
                
                foreach ( var c in _tempCompounds )
                {
                    var reqs = new (int KeyId, int ValueId)[c.reqs.Length];
                    bool isValid = true;

                    for ( int i = 0; i < c.reqs.Length; i++ )
                    {
                        if ( _keyMap.TryGetValue( c.reqs[i].Key, out int kId ) && 
                            _valueMap[kId].TryGetValue( c.reqs[i].Value, out int vId ) )
                        {
                            reqs[i] = (kId, vId);
                        }
                        else
                        {
                            Log.Warning( $"[Tailbox.CVA] Invalid compound rule ignored: Variant '{c.reqs[i].Key}' or value '{c.reqs[i].Value}' not found." );
                            isValid = false;
                            break;
                        }
                    }

                    if ( isValid )
                    {
                        finalCompounds.Add( new CompoundVariant { Requirements = reqs, Classes = c.classes } );
                    }
                }

                return new TailboxCva( _baseStyles, _variants.ToArray(), finalCompounds.ToArray(), _keyMap, _valueMap.ToArray() );
            }

            public static implicit operator TailboxCva( Builder builder ) => builder.Bake();
        }

        /// <summary>Builder for defining cases within a variant.</summary>
        public class VariantBuilder
        {
            internal Dictionary<string, string> Cases { get; } = new( StringComparer.OrdinalIgnoreCase );
            internal string DefaultCase { get; private set; }

            public VariantBuilder AddCase( string value, string classes ) 
            { 
                Cases[value] = classes; 
                return this; 
            }
            
            public VariantBuilder SetDefault( string value ) 
            { 
                DefaultCase = value; 
                return this; 
            }

            public VariantBuilder Case( string value, string classes, bool isDefault = false )
            {
                AddCase( value, classes );
                if ( isDefault ) SetDefault( value );
                return this;
            }

            public VariantBuilder IsolatedCase( string value, string classes, bool isDefault = false )
            {
                return Case( value, classes, isDefault );
            }
        }

        /// <summary>
        /// A lightweight, ref-struct resolver to handle fluent CVA property mapping without allocations.
        /// </summary>
        public ref struct CvaResolver
        {
            private readonly TailboxCva _cva;

            internal CvaResolver( TailboxCva cva )
            {
                _cva = cva;
            }

            /// <summary>Specifies a value for a specific variant key.</summary>
            public CvaResolver With( string key, string value )
            {
                if ( value == null ) return this;
                
                // Map strings to integer IDs immediately during resolution
                if ( _cva._variantKeyToId.TryGetValue( key, out int keyId ) )
                {
                    if ( keyId < _cva._variantValueToId.Length && _cva._variantValueToId[keyId].TryGetValue( value, out int valueId ) )
                    {
                        if ( keyId < _tsActiveValueIds.Length )
                        {
                            _tsActiveValueIds[keyId] = valueId;
                        }
                    }
                }
                return this;
            }

            public CvaResolver With( string key, bool value ) => With( key, value ? "true" : "false" );

            /// <summary>Specifies an enum value, utilizing a high-performance string cache.</summary>
            public CvaResolver With<TEnum>( string key, TEnum value ) where TEnum : struct, Enum 
                => With( key, EnumStringCache<TEnum>.GetString( value ) );

            /// <summary>Executes the CVA resolution logic and returns a styled result.</summary>
            public CvaResult Build( string extraClass = null ) 
            {
                string cachedBase = _cva.GetOrBuildCachedString();
                return new CvaResult( cachedBase, extraClass );
            }
        }

        private string GetOrBuildCachedString()
        {
            ulong stateHash = 0;
            
            // Generate permutation hash using pre-calculated bit-masks and offsets
            for ( int i = 0; i < _bakedVariants.Length; i++ )
            {
                ref var variant = ref _bakedVariants[i];
                int activeValId = _tsActiveValueIds[i];
                if ( activeValId == 0 ) activeValId = variant.DefaultValueId;

                stateHash |= ((ulong)(activeValId & variant.BitMask) << variant.BitOffset);
            }

            // O(1) Lookup: Check if this exact combination has already been computed
            if ( _permutationCache.TryGetValue( stateHash, out string cachedResult ) )
            {
                return cachedResult;
            }

            return BuildAndCacheString( stateHash );
        }

        private string BuildAndCacheString( ulong stateHash )
        {
            _tsBuffer.Clear();
            _tsBuffer.Append( _baseStyles );

            // Compose standard variants
            for ( int i = 0; i < _bakedVariants.Length; i++ )
            {
                ref var variant = ref _bakedVariants[i];
                int activeValId = _tsActiveValueIds[i];
                if ( activeValId == 0 ) activeValId = variant.DefaultValueId;

                if ( activeValId != 0 && variant.ClassesById.TryGetValue( activeValId, out var classes ) )
                {
                    AppendSpaceIfNeeded();
                    _tsBuffer.Append( classes );
                }
            }

            // Process compound rules using pure integer logic
            for ( int i = 0; i < _bakedCompounds.Length; i++ )
            {
                ref var compound = ref _bakedCompounds[i];
                bool match = true;

                for ( int j = 0; j < compound.Requirements.Length; j++ )
                {
                    var req = compound.Requirements[j];
                    int activeValId = _tsActiveValueIds[req.KeyId];
                    if ( activeValId == 0 ) activeValId = _bakedVariants[req.KeyId].DefaultValueId;

                    if ( activeValId != req.ValueId )
                    {
                        match = false;
                        break;
                    }
                }

                if ( match )
                {
                    AppendSpaceIfNeeded();
                    _tsBuffer.Append( compound.Classes );
                }
            }

            string finalResult = _tsBuffer.ToString();
            
            // Atomic update of the permutation cache
            _permutationCache[stateHash] = finalResult;

            return finalResult;
        }

        private void AppendSpaceIfNeeded()
        {
            if ( _tsBuffer.Length > 0 && _tsBuffer[_tsBuffer.Length - 1] != ' ' )
                _tsBuffer.Append( ' ' );
        }

        private static class EnumStringCache<T> where T : struct, Enum
        {
            private static readonly ConcurrentDictionary<T, string> _cache = new();
            
            /// <summary>Retrieves a low-invariant string representation of the enum with zero runtime allocation after first call.</summary>
            public static string GetString( T value )
            {
                return _cache.GetOrAdd( value, v => v.ToString().ToLowerInvariant() );
            }
        }
    }
}