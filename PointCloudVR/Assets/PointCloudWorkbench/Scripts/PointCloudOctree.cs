using System;
using System.Collections.Generic;
using UnityEngine;

namespace PointCloudWorkbench
{
    public class PointCloudOctree
    {
        public class Node
        {
            public Bounds bounds;
            public Vector3 center;
            public float radius;
            public int level;
            
            // このノードに属する（自身でサンプリングされた）元の点群データのインデックス
            public List<int> pointIndices = new List<int>();
            
            // 子ノード（8個、nullの場合は葉ノード）
            public Node[] children;
            public bool isLeaf => children == null;

            public Node(Bounds bounds, int level)
            {
                this.bounds = bounds;
                this.center = bounds.center;
                // 境界球の半径として、バウンディングボックスの対角線半分の長さを設定
                this.radius = bounds.extents.magnitude;
                this.level = level;
            }
        }

        public Node root { get; private set; }
        public int maxLevel { get; private set; }
        public int maxPointsPerNode { get; private set; }
        public bool isBuilt { get; private set; }

        public PointCloudOctree()
        {
            isBuilt = false;
        }

        /// <summary>
        /// 点群データからオクトリーを構築する（別スレッドからの呼び出しに対応）
        /// </summary>
        public void Build(Vector3[] positions, int maxPointsPerNode = 512, int maxLevel = 8)
        {
            isBuilt = false;
            this.maxPointsPerNode = maxPointsPerNode;
            this.maxLevel = maxLevel;

            if (positions == null || positions.Length == 0)
            {
                root = null;
                return;
            }

            // 1. 全体を包む境界ボックス（AABB）の算出
            Vector3 min = positions[0];
            Vector3 max = positions[0];
            for (int i = 1; i < positions.Length; i++)
            {
                Vector3 p = positions[i];
                if (p.x < min.x) min.x = p.x;
                if (p.y < min.y) min.y = p.y;
                if (p.z < min.z) min.z = p.z;
                
                if (p.x > max.x) max.x = p.x;
                if (p.y > max.y) max.y = p.y;
                if (p.z > max.z) max.z = p.z;
            }

            Bounds bounds = new Bounds((min + max) * 0.5f, max - min);
            
            // アスペクト比の偏りを防ぎ、8分割が均等に行われるように立方体にする
            float maxExt = Mathf.Max(bounds.extents.x, Mathf.Max(bounds.extents.y, bounds.extents.z));
            bounds.extents = new Vector3(maxExt, maxExt, maxExt);

            root = new Node(bounds, 0);

            // インデックスの初期リスト
            List<int> initialIndices = new List<int>(positions.Length);
            for (int i = 0; i < positions.Length; i++)
            {
                initialIndices.Add(i);
            }

            // 2. 再帰的なサブディビジョン（分割）の開始
            Subdivide(root, initialIndices, positions);

            isBuilt = true;
        }

        private void Subdivide(Node node, List<int> indices, Vector3[] positions)
        {
            if (indices.Count == 0) return;

            // このノードに留める代表点の数を決定
            int targetCount = Mathf.Min(indices.Count, maxPointsPerNode);
            
            // 空間的に均一にサンプリングするため、一定間隔（ストライド）で点を抽出する
            float stride = (float)indices.Count / targetCount;
            HashSet<int> selectedIndicesInList = new HashSet<int>();
            
            for (int i = 0; i < targetCount; i++)
            {
                int listIdx = Mathf.Clamp((int)(i * stride), 0, indices.Count - 1);
                if (!selectedIndicesInList.Contains(listIdx))
                {
                    node.pointIndices.Add(indices[listIdx]);
                    selectedIndicesInList.Add(listIdx);
                }
            }

            // 残りの（サンプリングされなかった）点のインデックスを抽出
            List<int> remainingIndices = new List<int>(indices.Count - node.pointIndices.Count);
            for (int i = 0; i < indices.Count; i++)
            {
                if (!selectedIndicesInList.Contains(i))
                {
                    remainingIndices.Add(indices[i]);
                }
            }

            // 最大階層に達した、あるいは残りの点が無ければ、これ以上分割せず葉ノードとする
            if (node.level >= maxLevel || remainingIndices.Count == 0)
            {
                // 残りの点をすべてこのノードに格納して終了
                node.pointIndices.AddRange(remainingIndices);
                return;
            }

            // 8つの子ノードの作成
            node.children = new Node[8];
            Vector3 parentCenter = node.bounds.center;
            Vector3 childSize = node.bounds.size * 0.5f;
            Vector3 childExtents = node.bounds.extents * 0.5f;

            for (int i = 0; i < 8; i++)
            {
                // ビットパターンで8つの象限を特定する
                // i & 1 -> X軸方向 (-X / +X)
                // i & 2 -> Y軸方向 (-Y / +Y)
                // i & 4 -> Z軸方向 (-Z / +Z)
                float offsetX = ((i & 1) == 0) ? -childExtents.x : childExtents.x;
                float offsetY = ((i & 2) == 0) ? -childExtents.y : childExtents.y;
                float offsetZ = ((i & 4) == 0) ? -childExtents.z : childExtents.z;

                Vector3 childCenter = parentCenter + new Vector3(offsetX, offsetY, offsetZ);
                node.children[i] = new Node(new Bounds(childCenter, childSize), node.level + 1);
            }

            // 子ノードへ点を分配
            List<int>[] childBuckets = new List<int>[8];
            for (int i = 0; i < 8; i++)
            {
                childBuckets[i] = new List<int>(remainingIndices.Count / 8);
            }

            foreach (int idx in remainingIndices)
            {
                Vector3 p = positions[idx];
                int bucketIdx = 0;
                if (p.x >= parentCenter.x) bucketIdx |= 1;
                if (p.y >= parentCenter.y) bucketIdx |= 2;
                if (p.z >= parentCenter.z) bucketIdx |= 4;

                childBuckets[bucketIdx].Add(idx);
            }

            // 子ノードごとに再帰的に分割処理を実行
            for (int i = 0; i < 8; i++)
            {
                Subdivide(node.children[i], childBuckets[i], positions);
            }
        }

        /// <summary>
        /// 指定した中心座標から半径以内にある点をオクトリーを利用して高速に収集する
        /// </summary>
        public void FindPointsWithinRadius(Node node, Vector3 localCenter, float localRadius, List<int> result, Vector3[] positions)
        {
            if (node == null) return;

            // ノード境界球とクエリ球の衝突判定
            float dist = Vector3.Distance(node.center, localCenter);
            if (dist > node.radius + localRadius)
            {
                return; // 交差しないためカリング
            }

            // このノードの代表点について距離判定
            foreach (int idx in node.pointIndices)
            {
                float distSq = (positions[idx] - localCenter).sqrMagnitude;
                if (distSq <= localRadius * localRadius)
                {
                    result.Add(idx);
                }
            }

            // 子ノードを再帰探索
            if (!node.isLeaf)
            {
                for (int i = 0; i < 8; i++)
                {
                    if (node.children[i] != null)
                    {
                        FindPointsWithinRadius(node.children[i], localCenter, localRadius, result, positions);
                    }
                }
            }
        }
    }
}
