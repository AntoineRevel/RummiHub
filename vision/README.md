# Mode photo : lire une vraie partie

But : pendant une vraie partie, le joueur photographie la table puis son chevalet. Le site lit
les tuiles, les donne au solveur et anime le meilleur coup. La lecture tourne sur notre serveur
avec un petit modèle (aucune API d'IA payante).

## Chaîne de lecture

1. **Détecteur** (YOLO « nano », une seule classe `tuile`, ~6 Mo en ONNX) : il trouve les
   rectangles des tuiles.
2. **Lecteur** (mini-CNN, < 1 Mo) : sur chaque tuile, découpée dans la photo en pleine
   résolution, il donne une probabilité pour chaque face (1 à 13, joker) et chaque couleur.
3. **Regroupement** (`RummiHub.Core/Vision/TileGrouper.cs`, sans IA) : des tuiles côte à côte
   sur une ligne forment une combinaison. Les combinaisons sont rangées dans l'ordre de lecture.
4. **Décodage sous contraintes** (`RummiHub.Core/Vision/ConstrainedDecoder.cs`) : il retient la
   lecture la plus probable qui respecte les règles. Chaque combinaison est une suite ou un
   groupe valide, chaque tuile existe en deux exemplaires au plus, il y a deux jokers au plus,
   et un joker posé reçoit la tuile qu'il remplace. Les règles ne contredisent le lecteur que là
   où il hésitait : une tuile lue avec certitude n'est jamais changée.
5. **Écran de vérification** : le joueur corrige d'un appui ce qui reste faux, puis le solveur
   habituel (`/solve`) et l'animation de la partie prennent le relais.

État : les étapes 3 et 4 sont écrites et testées (`RummiHub.Tests/Core/Vision`). Les modèles
(étapes 1 et 2) restent à entraîner, à partir des photos ci-dessous.

## Les photos à prendre (phase 0)

Les photos ne vont **pas** dans ce dépôt, qui est public. On les range à part.

### 1. Les tuiles seules (environ 1 h)

- Les 106 tuiles, à plat, faces visibles, 10 à 15 par photo, bien séparées, sur un fond uni.
- Deux passes complètes : une à la lumière du jour et une sous une lampe le soir. Si possible,
  une troisième sur la nappe ou la table où vous jouez d'habitude.
- Le téléphone au-dessus des tuiles, sans flash s'il fait des reflets.

Ces tuiles seront détourées puis recomposées sur des fonds variés (perspective, flou, lumière)
pour fabriquer des milliers d'images d'entraînement.

### 2. Des positions de vraies parties (le jeu de test)

Pour 20 à 30 moments de parties différentes :

- une photo de **toute la table**, prise d'au-dessus, toutes les combinaisons dans le cadre ;
- une photo du **chevalet** du joueur, de face ;
- le relevé exact, une ligne par combinaison, puis le chevalet :

```
R4 R5 R6
B9 R9 K9 J
---
B1 K13 O7 J
```

Couleurs : `B` bleu, `R` rouge, `O` orange, `K` noir ; `J` pour un joker. Ces positions ne servent
jamais à l'entraînement : elles mesurent la lecture. On ne passe au site que si, sur elles,
plus de 98 % des tuiles sont justes après décodage et plus de 70 % des photos sont lues sans
aucune retouche.

## Jeux de données publics (à vérifier avant usage)

- Roboflow « Rummikub » (jonasbuehler, v8, ~565 images) et « Rummy Tiles Detector » (171 images,
  CC BY 4.0).
- Le jeu Kaggle de l'article *Enhancing Computer Vision with Knowledge: a Rummikub Case Study*
  (arXiv 2411.18172) : 285 photos, 4 336 tuiles annotées. Cet article utilise lui aussi les
  règles du jeu pour corriger la vision.

Vérifier la licence de chacun avant de l'utiliser. On ne les recopie jamais dans le dépôt.

## Entraînement

Sur un GPU gratuit (Colab, Kaggle) ou sur un PC, jamais sur le serveur de production. Les
scripts (`vision/*.py`) et l'export ONNX viendront avec la première série de photos.
