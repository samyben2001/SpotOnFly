# SpotOnFly

[![CI](https://github.com/samyben2001/SpotOnFly/actions/workflows/ci.yml/badge.svg)](https://github.com/samyben2001/SpotOnFly/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/samyben2001/SpotOnFly)](https://github.com/samyben2001/SpotOnFly/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Petite application Windows sans fenêtre principale : elle vit dans la zone de notification
(system tray), détecte le média en cours de lecture sur le PC et affiche son titre dans un
overlay discret.

## Fonctionnalités

- Détection automatique du média en cours via l'API Windows **GSMTC**
  (Global System Media Transport Controls) : Spotify, Chrome, Edge, Firefox, lecteurs média…
- Overlay discret avec pochette, titre, artiste et contrôles **précédent / lecture-pause / suivant**.
- Pochette fournie par Windows quand elle existe, sinon recherchée automatiquement sur l'API
  publique Deezer (cache mémoire et disque).
- Taille adaptative selon la zone de travail, et repositionnement automatique lors d'un
  changement de résolution, de mise à l'échelle (DPI) ou de barre des tâches.
- Trois tailles d'overlay au choix (petite, moyenne, grande) et durée d'affichage réglable
  de 1 à 5 secondes depuis le menu du tray.
- Sur les écrans étroits, la pochette se masque pour préserver le titre et les boutons.
- Zones transparentes **click-through** : la souris n'est interceptée que sur la carte (boutons).
- N'apparaît pas dans Alt+Tab et ne vole pas le focus.
- Fondu d'apparition / disparition, et disparition automatique suspendue tant que la souris survole l'overlay.
- Affichage automatique à chaque changement de piste (y compris reprise de lecture).
- Icône de notification avec menu : activer/désactiver, position, taille, durée d'affichage,
  test, démarrage auto, quitter.
- Infobulle du tray mise à jour avec la piste en cours.
- Réglages persistés dans `%AppData%\SpotOnFly\settings.json`.
- Journal dans `%LocalAppData%\SpotOnFly\app.log`.
- Instance unique.

## Téléchargement

La dernière version est disponible dans les
[releases GitHub](https://github.com/samyben2001/SpotOnFly/releases/latest).

1. Télécharger `SpotOnFly-vX.Y.Z-win-x64.zip`.
2. Décompresser l'archive.
3. Lancer `SpotOnFly.exe`.

L'exécutable est **autonome** : aucun runtime .NET n'est nécessaire. Il n'est pas signé
numériquement, donc Windows SmartScreen peut afficher un avertissement au premier lancement :
cliquer sur **Informations complémentaires** puis **Exécuter quand même**.

## Prérequis

- Windows 10 1809 (build 17763) ou supérieur.
- Aucun prérequis supplémentaire pour la release autonome.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) uniquement pour compiler ou lancer
  depuis les sources.

## Compiler et lancer

```powershell
dotnet run --project SpotOnFly.csproj
```

L'application démarre directement dans le tray (aucune fenêtre). Clic droit sur l'icône ♪
pour ouvrir le menu.

Pour tester l'overlay sans musique : `dotnet run --project SpotOnFly.csproj -- --preview`.

## Tests

```powershell
dotnet test SpotOnFly.slnx
```

Les tests couvrent la lecture tolérante des réglages (valeurs d'enum inconnues, bornes),
la logique de correspondance des métadonnées avec Deezer, le cache et la pause anti-limite
de l'API, le calcul de taille/position de l'overlay, le filtrage des applications ignorées
et les décisions d'affichage/réaffichage de piste.

Ces tests sont exécutés automatiquement par GitHub Actions à chaque push et pull request
(workflow [CI](.github/workflows/ci.yml)).

## Publier un exécutable

Framework-dependent (nécessite le .NET 10 Desktop Runtime, ~15 Mo) :

```powershell
dotnet publish SpotOnFly.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Autonome (aucun runtime requis, ~90 Mo) :

```powershell
dotnet publish SpotOnFly.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

L'exécutable se trouve dans `publish\SpotOnFly.exe`.

Les releases GitHub sont générées automatiquement par le workflow
[Release](.github/workflows/release.yml) lors du push d'un tag `v*`. Un tag `v0.1.0` produit
l'archive `SpotOnFly-v0.1.0-win-x64.zip`.

## Configuration

Le fichier `%AppData%\SpotOnFly\settings.json` est créé au premier réglage. Champs disponibles :

| Champ | Description | Défaut |
|---|---|---|
| `Enabled` | Overlay activé | `true` |
| `Position` | `TopLeft`, `TopCenter`, `TopRight`, `BottomLeft`, `BottomCenter`, `BottomRight` | `BottomCenter` |
| `Size` | `Small`, `Medium`, `Large` | `Medium` |
| `HoldSeconds` | Durée d'affichage (1 à 5 s) | `4` |
| `EdgeMargin` | Marge avec le bord de l'écran (DIP) | `8` |
| `ShowArtist` | Afficher l'artiste | `true` |
| `RunAtStartup` | Lancer au démarrage de Windows | `false` |
| `IgnoredApps` | Identifiants d'apps à ignorer (ex. `Spotify.exe`) | `[]` |

## Pochettes d'album

Windows (GSMTC) expose la pochette de la plupart des lecteurs. Certaines applications, dont
Spotify, ne la fournissent pas toujours : SpotOnFly interroge alors l'API publique **Deezer**
avec le titre et l'artiste, sélectionne la meilleure correspondance et affiche l'image dès
qu'elle est disponible, sans retarder l'apparition de l'overlay.

Les pochettes téléchargées sont mises en cache dans `%LocalAppData%\SpotOnFly\artwork`, ce qui
rend les affichages suivants instantanés. Le cache disque conserve jusqu'à 500 pochettes
(50 Mo maximum), et les recherches sans résultat sont mémorisées 7 jours pour éviter de
réinterroger l'API inutilement. En cas de réponse HTTP 429 de Deezer, les recherches en ligne
sont suspendues 10 minutes (ou selon l'en-tête `Retry-After`) ; les pochettes GSMTC et le cache
restent disponibles. Supprimer le dossier vide tous les caches. Une connexion Internet n'est
nécessaire que pour les pistes sans pochette GSMTC ; seuls le titre, l'artiste et l'album sont
envoyés à Deezer.

## Limites connues

- L'overlay n'est plus entièrement click-through : la carte (boutons) capte la souris.
  Les zones transparentes autour de la carte laissent toujours passer les clics.
- L'API GSMTC ne remonte que les applications intégrées aux sessions média de Windows.
  Une application qui ne s'y déclare pas ne sera pas détectée.
- La recherche de pochette en ligne dépend du catalogue Deezer : une piste rare ou absente
  peut conserver le placeholder. Sans connexion Internet, seules les pochettes GSMTC et
  celles déjà en cache sont disponibles.
- L'overlay s'affiche par-dessus les applications, les navigateurs et les jeux en plein écran
  composité (Fullscreen Optimizations, le cas de la grande majorité des jeux modernes).
  Le **plein écran exclusif pur** (FSO désactivé) nécessiterait un hook Direct3D — prévu en
  phase 2.
- L'overlay utilise l'écran où se trouve sa fenêtre (l'écran principal au lancement) ;
  le choix de l'écran n'est pas encore proposé dans le menu.

## Licence

SpotOnFly est distribué sous licence [MIT](LICENSE).

La release autonome embarque le runtime .NET / WPF et `H.NotifyIcon.Wpf`, également sous
licence MIT. Leurs mentions sont regroupées dans
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
