# Transcriber

## Accessibility audit on every UI change

Any change to the UI (XAML in `src/Transcriber.App` or `src/Transcriber.Mobile`, styles, colours, themes,
or code that sets colours or builds controls) gets a WCAG 2.1 AA audit before it's committed, reported with
the change. Check, for each app and both light and dark themes:

- **Contrast**, measured from the actual colour values, not eyeballed: text 4.5:1 (3:1 at 18pt, or 14pt
  bold), and 3:1 for the text cursor, focus indicators, field outlines and anything else needed to see or
  use a control (1.4.3, 1.4.11). Alpha colours (the Fluent `TextFillColor…` brushes) are blended over their
  real background first.
- **Screen readers.** On the phone, every control has a name. Text fields take `SemanticProperties.Hint`,
  never `Description`, which breaks TalkBack's editing actions on Android. Switches and pickers take
  `Description`, and section titles are headings. On the desktop, every control has a visible label or an
  `AutomationProperties.Name`.
- **Keyboard and focus** on the desktop: everything is reachable and the focus is visible.
- **Touch targets** on the phone are at least 44×44.

Where the colours are:
- **Phone:** `Resources/Styles/Colors.xaml` and `Styles.xaml`, `App.xaml`, and
  `Platforms/Android/Resources/values/colors.xml`. Android draws the text cursor, handles and focused
  underline in `colorAccent`, and `Platforms/Android/TextFieldColors.cs` tints them to match.
- **Desktop:** WPF-UI's Fluent theme. Use `TextFillColorSecondaryBrush`, not `TextFillColorTertiaryBrush`,
  for hint text, because tertiary is 3.3:1 in the light theme.

Say what couldn't be verified without a device or a screen reader.
