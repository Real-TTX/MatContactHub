namespace MatContactHub.Services.Ldap;

/// <summary>The three values of a filter (RFC 4511, 4.5.1.7): an entry is returned only when the filter is <see cref="True"/>.</summary>
public enum LdapTruth
{
    False,
    True,
    Undefined,
}

/// <summary>A filter made ready for many entries (the attribute names resolved, the values prepared, the numbers parsed once).</summary>
public abstract class LdapMatcher
{
    public abstract LdapTruth Evaluate(LdapEntry entry);

    public bool Matches(LdapEntry entry) => Evaluate(entry) == LdapTruth.True;
}

/// <summary>
/// Evaluates search filters on the entries of the directory. Attribute names without case and by alias or OID (<see cref="LdapAttributes.Canonical"/>), values with
/// caseIgnoreMatch / caseIgnoreSubstringsMatch / caseIgnoreOrderingMatch, the telephone numbers as numbers (<see cref="LdapPhone"/>). A filter on an attribute the
/// directory does not know is Undefined (so <c>(!(foo=x))</c> finds nothing); an extensible match with a matching rule or with <c>:dn</c> is Undefined too
/// (no entry matches it), without a rule it is an equality match.
/// </summary>
public static class LdapFilterEvaluator
{
    public static LdapMatcher Compile(LdapFilter filter, LdapPhone phone) => filter switch
    {
        LdapFilter.And a => new AndMatcher([.. a.Filters.Select(f => Compile(f, phone))]),
        LdapFilter.Or o => new OrMatcher([.. o.Filters.Select(f => Compile(f, phone))]),
        LdapFilter.Not n => new NotMatcher(Compile(n.Filter, phone)),
        LdapFilter.Equality e => new EqualityMatcher(e.Attribute, e.Value, phone, approximate: false),
        LdapFilter.Approx x => new EqualityMatcher(x.Attribute, x.Value, phone, approximate: true),
        LdapFilter.Substrings s => new SubstringMatcher(s, phone),
        LdapFilter.GreaterOrEqual g => new OrderingMatcher(g.Attribute, g.Value, greater: true),
        LdapFilter.LessOrEqual l => new OrderingMatcher(l.Attribute, l.Value, greater: false),
        LdapFilter.Present p => new PresentMatcher(p.Attribute),
        LdapFilter.Extensible m when m.MatchingRule is null && !m.DnAttributes && m.Attribute is not null => new EqualityMatcher(m.Attribute, m.Value, phone, false),
        _ => new UndefinedMatcher(),
    };

    /// <summary>Does the filter match the entry (true only for <see cref="LdapTruth.True"/>)?</summary>
    public static bool Matches(LdapFilter filter, LdapEntry entry, LdapPhone phone) => Compile(filter, phone).Matches(entry);

    private sealed class AndMatcher(LdapMatcher[] parts) : LdapMatcher
    {
        public override LdapTruth Evaluate(LdapEntry entry)
        {
            var result = LdapTruth.True;
            foreach (var p in parts)
            {
                switch (p.Evaluate(entry))
                {
                    case LdapTruth.False:
                        return LdapTruth.False;
                    case LdapTruth.Undefined:
                        result = LdapTruth.Undefined;
                        break;
                }
            }

            return result;
        }
    }

    private sealed class OrMatcher(LdapMatcher[] parts) : LdapMatcher
    {
        public override LdapTruth Evaluate(LdapEntry entry)
        {
            var result = LdapTruth.False;
            foreach (var p in parts)
            {
                switch (p.Evaluate(entry))
                {
                    case LdapTruth.True:
                        return LdapTruth.True;
                    case LdapTruth.Undefined:
                        result = LdapTruth.Undefined;
                        break;
                }
            }

            return result;
        }
    }

    private sealed class NotMatcher(LdapMatcher inner) : LdapMatcher
    {
        public override LdapTruth Evaluate(LdapEntry entry) => inner.Evaluate(entry) switch
        {
            LdapTruth.True => LdapTruth.False,
            LdapTruth.False => LdapTruth.True,
            _ => LdapTruth.Undefined,
        };
    }

    private sealed class UndefinedMatcher : LdapMatcher
    {
        public override LdapTruth Evaluate(LdapEntry entry) => LdapTruth.Undefined;
    }

    /// <summary>The attribute of a filter item: its canonical name and whether the directory knows it.</summary>
    private abstract class ItemMatcher(string description) : LdapMatcher
    {
        protected string Name { get; } = LdapAttributes.Canonical(description);

        protected bool Known => LdapAttributes.IsKnown(Name);

        public override LdapTruth Evaluate(LdapEntry entry)
        {
            var values = entry.Get(Name);
            if (values is null)
            {
                return Known ? LdapTruth.False : LdapTruth.Undefined;
            }

            return Test(values) ? LdapTruth.True : LdapTruth.False;
        }

        protected abstract bool Test(LdapValues values);

        protected static LdapTruth Of(bool value) => value ? LdapTruth.True : LdapTruth.False;
    }

    private sealed class PresentMatcher(string description) : ItemMatcher(description)
    {
        public override LdapTruth Evaluate(LdapEntry entry) => Of(entry.Get(Name) is not null);

        protected override bool Test(LdapValues values) => true;
    }

    private sealed class EqualityMatcher : ItemMatcher
    {
        private readonly string _assertion;
        private readonly string _approximate;
        private readonly LdapPhoneValue? _phone;
        private readonly bool _isApproximate;

        public EqualityMatcher(string description, string assertion, LdapPhone phone, bool approximate) : base(description)
        {
            _isApproximate = approximate;
            _assertion = LdapMatch.Prepare(assertion);
            _approximate = approximate ? LdapMatch.Approximate(assertion) : string.Empty;
            if (LdapAttributes.IsPhone(Name) && Contacts.PhoneNormalizer.Digits(assertion).Length > 0)
            {
                _phone = phone.Prepare(assertion);
            }
        }

        protected override bool Test(LdapValues values)
        {
            for (var i = 0; i < values.Values.Length; i++)
            {
                if (_phone is not null && values.Phones is { } phones)
                {
                    if (LdapPhone.Equal(phones[i], _phone))
                    {
                        return true;
                    }

                    continue;
                }

                if (_isApproximate ? LdapMatch.Approximate(values.Values[i]) == _approximate : LdapMatch.EqualsIgnoreCase(values.Values[i], _assertion))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class SubstringMatcher : ItemMatcher
    {
        private readonly string? _initial;
        private readonly string[] _any;
        private readonly string? _final;
        private readonly IReadOnlyList<LdapPhonePattern> _patterns = [];

        public SubstringMatcher(LdapFilter.Substrings filter, LdapPhone phone) : base(filter.Attribute)
        {
            _initial = filter.Initial is null ? null : LdapMatch.Prepare(filter.Initial);
            _any = [.. filter.Any.Select(LdapMatch.Prepare)];
            _final = filter.Final is null ? null : LdapMatch.Prepare(filter.Final);
            if (LdapAttributes.IsPhone(Name))
            {
                _patterns = phone.Patterns(filter.Initial, filter.Any, filter.Final);
            }
        }

        protected override bool Test(LdapValues values)
        {
            if (_patterns.Count > 0 && values.Phones is { } phones)
            {
                return phones.Any(p => LdapPhone.Matches(p, _patterns));
            }

            return values.Values.Any(v => LdapMatch.Substring(LdapMatch.Prepare(v), _initial, _any, _final, StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class OrderingMatcher(string description, string assertion, bool greater) : ItemMatcher(description)
    {
        private readonly string _assertion = LdapMatch.Prepare(assertion);

        protected override bool Test(LdapValues values)
            => values.Values.Any(v =>
            {
                var c = string.Compare(LdapMatch.Prepare(v), _assertion, StringComparison.OrdinalIgnoreCase);
                return greater ? c >= 0 : c <= 0;
            });
    }
}
