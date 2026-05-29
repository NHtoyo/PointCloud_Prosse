# 調査レポート: 点群の超高速接続探索（Connected Component Labeling）

## 調査目的
点群の接続探索（Region Growing, Connected Component Labeling）を極めて効率的に行う手法を調査する。Octree再帰BFSによる遅延を解消し、数百万点スケールでも高速処理可能な、PCL等で実績のあるC#実装方針を策定する。

## 検索語
- PCL Euclidean Cluster Extraction octree radius search connected components point cloud
- CloudCompare DgmOctree extractCCs source AutoSegmentationTools labelConnectedComponents
- FEC Fast Euclidean Clustering for Point Cloud Segmentation arXiv 2208.07678 algorithm pointwise scheme
- GitHub C# SpatialHash Vector3 Radius search Unity

## 参照URL
- PCL Euclidean Cluster Extraction: https://pcl.readthedocs.io/projects/tutorials/en/latest/cluster_extraction.html
- PCL Octree Search: https://pointclouds.org/documentation/tutorials/octree.html
- CloudCompare Label Connected Components: https://www.cloudcompare.org/doc/wiki/index.php/Label_Connected_Components
- CloudCompare Octree: https://www.cloudcompare.org/doc/wiki/index.php?title=CloudCompare_octree
- CloudCompare AutoSegmentationTools: https://www.cloudcompare.org/doc/CCLib/html/class_c_c_core_lib_1_1_auto_segmentation_tools.html
- CloudCompare connectivity forum: https://www.cloudcompare.net/forum/viewtopic.php?t=2727
- Autoware Spatial Hash: https://autowarefoundation.gitlab.io/autoware.auto/AutowareAuto/geometry-spatial-hash.html
- Efficient Radius Neighbor Search: https://jbehley.github.io/papers/behley2015icra.pdf

## 分かったこと
- 第一候補は `Spatial Hash + BFS`。
- セルサイズをまず `localRadius` にし、現在セル + 周囲26セルだけを見て、最後に二乗距離で厳密判定する。
- パフォーマンスのために `Queue<int>` や `HashSet<int>` のアロケーションを避け、単純な配列 (`int[] queue`, `bool[] visited`) とインデックスアクセスを使うのがC#/Unityで最速。
- 第二候補は `Voxel occupancy + Union-Find`。CloudCompareのアプローチに近く非常に高速だが、点間の厳密な距離ベースの連結ではなく「セルが隣接しているか」の近似になる。

## 実装に使う判断
最も汎用的で厳密な結果が得られる「Spatial Hash + BFS」を採用する。C# (Unity) 環境では `Dictionary<Vector3Int, List<int>>` を使うか、並列処理を前提に `NativeParallelMultiHashMap` を使うとさらに高速化が見込める。

## 不確かな点
- `NativeParallelMultiHashMap` は高速だが、Burst Compiler等との依存関係がある。標準の `Dictionary` とのパフォーマンス差が実用上どこまで影響するかは環境依存。

## 次にAntigravityが行うこと
この調査内容を保存し、実装方針に従ってUnity側のコードを修正する。実装はまず既存のOctree版を残したまま、比較用に `Spatial Hash + BFS` の新規クラスまたは関数を追加し、安全にテストすること。
