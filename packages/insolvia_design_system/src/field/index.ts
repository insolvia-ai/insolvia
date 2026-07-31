// ERGONOMICS NOTE (measured in the adoption spike): Field shares only
// field.props — the id triplet, the FieldContext shape, and the describedby
// composition rule. The five rendered parts, Root's child-presence scan (it
// compares against each leaf's OWN Description/Error identities), and the
// render-as-select/textarea escape hatch do NOT transfer. The shared module is
// ~40 lines; each leaf is ~120. That asymmetry is the honest finding.
export { Field } from './field';
export type { FieldRootOwnProps } from './field.props';
