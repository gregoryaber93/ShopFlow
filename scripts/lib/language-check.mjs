// Detects non-English (Polish) text in repository files.
//
// WHY: the repository language is English. Customer-facing Polish strings are a deliberate exception and are listed
// in scripts/english-allowlist.json; everything else (comments, docs, ADRs, runbooks, Claude instructions) is English.
// Used by scripts/check-english.mjs (whole repository, CI-style) and .claude/hooks/check-english.mjs (one edited file).

import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative, sep, extname, basename } from "node:path";

const DIACRITICS = /[ąćęłńóśźżĄĆĘŁŃÓŚŹŻ]/;

// Polish words written without diacritics that are not English words and rarely appear as identifiers.
// A line is reported when it contains two or more distinct words from this list (one word alone gives false positives).
const POLISH_WORDS = new Set(
  `jest nie oraz albo lub przez tylko jezeli jesli gdy ktory ktora ktore ktorych ktorego sie tego tej tym jako czy
  zeby aby wiec juz moze musi kazdy kazda kazde wszystkie wszystko bez dla takze rowniez potem najpierw zawsze nigdy
  tutaj teraz jeszcze bardzo dlatego poniewaz zamiast miedzy przed podczas wtedy jego jej ich sa byl byla bylo beda
  zamowienie zamowienia zamowien wiadomosc wiadomosci kolejka kolejki kolejce zdarzenie zdarzenia zdarzen uslugi usluga
  platnosc platnosci wdrozenie wdrozenia srodowisko srodowiska uprawnienia tozsamosc tozsamosci dostawca dostawcy
  odpowiedz odpowiedzi blad bledu bledy ponowienie ponowienia przetwarzanie kontener kontenera baza bazy danych klient
  klienta klientow magazyn magazynu przesylka przesylki faktura faktury rezerwacja rezerwacji sekretu wynik podpis
  uwaga przyklad krok kroki zobacz patrz uruchom sprawdz dodaj zmien usun plik pliki katalog wersja wersji`
    .split(/\s+/)
    .filter(Boolean),
);

const TEXT_EXTENSIONS = new Set([
  ".cs", ".md", ".bicep", ".bicepparam", ".json", ".yml", ".yaml", ".xml", ".kql", ".sql", ".sh", ".ps1", ".mjs",
  ".js", ".props", ".targets", ".csproj", ".slnx", ".http", ".txt", ".editorconfig", ".gitignore", ".gitattributes",
]);
const TEXT_FILE_NAMES = new Set(["Dockerfile", "CODEOWNERS", ".gitignore", ".dockerignore", ".editorconfig", ".gitattributes"]);
const SKIPPED_DIRECTORIES = new Set([
  ".git", "bin", "obj", "node_modules", "TestResults", ".vs", ".idea", "artifacts", "publish", "coverage",
]);

const COMMENT_LINE = /^\s*(\/\/|\/\*|\*|#|<!--|--)/;

export function isTextFile(path) {
  const name = basename(path);
  return TEXT_FILE_NAMES.has(name) || TEXT_EXTENSIONS.has(extname(name)) || name.startsWith("Dockerfile");
}

export function loadAllowlist(root) {
  const raw = JSON.parse(readFileSync(join(root, "scripts", "english-allowlist.json"), "utf8"));
  return new Map(raw.files.map((entry) => [entry.path, entry]));
}

export function toRepoPath(root, absolutePath) {
  return relative(root, absolutePath).split(sep).join("/");
}

export function* walk(root, directory = root) {
  for (const name of readdirSync(directory)) {
    const fullPath = join(directory, name);
    const stats = statSync(fullPath);
    if (stats.isDirectory()) {
      if (!SKIPPED_DIRECTORIES.has(name)) yield* walk(root, fullPath);
    } else if (isTextFile(fullPath)) {
      yield fullPath;
    }
  }
}

function polishWordsIn(line) {
  const found = new Set();
  for (const word of line.toLowerCase().match(/[a-z]+/g) ?? []) {
    if (POLISH_WORDS.has(word)) found.add(word);
  }
  return [...found];
}

// Returns the findings for one file: [{ line, text, reason }].
// Allowlist modes: "skip" ignores the file; "comments-only" checks only comment lines (Polish string literals are allowed).
export function scanText(repoPath, text, allowlist) {
  const entry = allowlist.get(repoPath);
  if (entry?.mode === "skip") return [];

  const findings = [];
  text.split(/\r?\n/).forEach((rawLine, index) => {
    if (entry?.mode === "comments-only" && !COMMENT_LINE.test(rawLine)) return;
    // In allowlisted files a comment may quote the Polish text it explains (e.g. "zł"); quoted fragments are ignored.
    const line = entry?.mode === "comments-only" ? rawLine.replace(/"[^"]*"/g, '""') : rawLine;
    if (DIACRITICS.test(line)) {
      findings.push({ line: index + 1, text: rawLine.trim(), reason: "Polish diacritics" });
      return;
    }
    const words = polishWordsIn(line);
    if (words.length >= 2) {
      findings.push({ line: index + 1, text: rawLine.trim(), reason: `Polish words: ${words.join(", ")}` });
    }
  });
  return findings;
}

export function scanFile(root, absolutePath, allowlist) {
  return scanText(toRepoPath(root, absolutePath), readFileSync(absolutePath, "utf8"), allowlist);
}
