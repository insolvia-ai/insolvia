// Runs before every test file, after jest-expo's own setup.
//
// THE WINDOW IS A DESKTOP. React Native's test environment reports a phone
// (750×1334), and the shell reads the width to decide whether the navigation
// rail starts collapsed — under `railBreakpoint` it does, which removes the
// wordmark, the case title and every label from the rail. Web is the only
// shipping target and a desktop window is what the deploy verification and
// the Lighthouse gates measure, so it is what the suite renders at too. A test
// that wants the narrow arm sets the dimensions itself.
const { Dimensions } = require('react-native');

const window = { width: 1280, height: 800, scale: 1, fontScale: 1 };
Dimensions.set({ window, screen: window });
