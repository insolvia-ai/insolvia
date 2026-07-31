// SHARED — no react-native / react-dom import.
export type CardElevation = 'flat' | 'raised';

export const elevationStyles: Record<CardElevation, string> = {
  flat: 'shadow-none',
  raised: 'shadow-md',
};
