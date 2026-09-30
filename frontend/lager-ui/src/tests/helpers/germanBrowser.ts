/// <summary>
/// Testumgebung als deutschsprachiger Browser: jsdom meldet "en-US", die Spracherkennung (src/i18n) würde daher Englisch
/// wählen. Die bestehenden Tests suchen die Oberfläche per deutschem Text — sie laufen mit der Standardsprache Deutsch.
/// Muss VOR dem ersten Import von src/i18n ausgewertet werden (setup.ts importiert es zuerst).
/// </summary>
for (const [name, value] of [['language', 'de-DE'], ['languages', ['de-DE', 'de']]] as const) {
  Object.defineProperty(window.navigator, name, { value, configurable: true })
}
