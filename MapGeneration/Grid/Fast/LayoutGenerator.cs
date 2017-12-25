﻿namespace MapGeneration.Grid.Fast
{
	using System;
	using System.Collections.Generic;
	using System.Collections.Specialized;
	using System.Linq;
	using Common;
	using GeneralAlgorithms.Algorithms.Graphs.GraphDecomposition;
	using GeneralAlgorithms.Algorithms.Polygons;
	using GeneralAlgorithms.DataStructures.Common;
	using GeneralAlgorithms.DataStructures.Graphs;
	using GeneralAlgorithms.DataStructures.Polygons;
	using Interfaces;
	using Utils;

	public class LayoutGenerator<TNode> : AbstractLayoutGenerator<TNode, GridPolygon, IntVector2>
	{
		protected readonly float ShapePerturbChance = 0.4f;
		protected IConfigurationSpaces<GridPolygon, Configuration, IntVector2> ConfigurationSpaces;
		protected IGraphDecomposer<int> GraphDecomposer = new DummyGraphDecomposer<int>();
		protected GridPolygonOverlap PolygonOverlap = new GridPolygonOverlap();

		private readonly int avgSize;
		private readonly int avgArea;
		private readonly float sigma;

		public LayoutGenerator(IConfigurationSpaces<GridPolygon, Configuration, IntVector2> configurationSpaces)
		{
			ConfigurationSpaces = configurationSpaces;
			ConfigurationSpaces.InjectRandomGenerator(Random);

			avgSize = GetAverageSize(configurationSpaces.GetAllShapes());
			avgArea = GetAverageArea(configurationSpaces.GetAllShapes());
			sigma = 100 * avgArea;
		}

		private int GetAverageSize(IEnumerable<GridPolygon> polygons)
		{
			return (int)polygons.Select(x => x.BoundingRectangle).Average(x => (x.Width + x.Height) / 2);
		}

		private int GetAverageArea(IEnumerable<GridPolygon> polygons)
		{
			return (int)polygons.Select(x => x.BoundingRectangle).Average(x => x.Area);
		}

		protected override Layout PerturbLayout(Layout layout, List<int> chain, out float energyDelta)
		{
			var node = chain.GetRandom(Random);
			var energy = layout.GetEnergy();
			var newLayout = Random.NextDouble() <= ShapePerturbChance ? PerturbShape(layout, node) : PerturbPosition(layout, node);

			var newEnergy = newLayout.GetEnergy();
			energyDelta = newEnergy - energy;

			return newLayout;
		}

		protected bool TooFar(Layout layout, List<int> chain, out Layout newLayout)
		{
			var maxY1 = int.MinValue;
			var maxY2 = int.MinValue;

			var minY1 = int.MaxValue;
			var minY2 = int.MaxValue;

			var maxX1 = int.MinValue;
			var maxX2 = int.MinValue;

			var minX1 = int.MaxValue;
			var minX2 = int.MaxValue;

			var firstChain = true;

			for (var i = 0; i < Graph.VerticesCount; i++)
			{
				if (!layout.GetConfiguration(i, out var configuration))
					continue;

				if (chain.Contains(i))
				{
					var rectangle = configuration.Polygon.BoundingRectangle;
					maxY1 = Math.Max(Math.Max(rectangle.B.Y + configuration.Position.Y, rectangle.A.Y + configuration.Position.Y), maxY1);
					minY1 = Math.Min(Math.Min(rectangle.B.Y + configuration.Position.Y, rectangle.A.Y + configuration.Position.Y), minY1);
					maxX1 = Math.Max(Math.Max(rectangle.B.X + configuration.Position.X, rectangle.A.X + configuration.Position.X), maxX1);
					minX1 = Math.Min(Math.Min(rectangle.B.X + configuration.Position.X, rectangle.A.X + configuration.Position.X), minX1);
				}
				else
				{
					firstChain = false;

					var rectangle = configuration.Polygon.BoundingRectangle;
					maxY2 = Math.Max(Math.Max(rectangle.B.Y + configuration.Position.Y, rectangle.A.Y + configuration.Position.Y), maxY2);
					minY2 = Math.Min(Math.Min(rectangle.B.Y + configuration.Position.Y, rectangle.A.Y + configuration.Position.Y), minY2);
					maxX2 = Math.Max(Math.Max(rectangle.B.X + configuration.Position.X, rectangle.A.X + configuration.Position.X), maxX2);
					minX2 = Math.Min(Math.Min(rectangle.B.X + configuration.Position.X, rectangle.A.X + configuration.Position.X), minX2);
				}
			}

			const double scale = 0.9;
			var minDiff = 4 * avgSize;

			var diffY = minY1 - maxY2 > 0 || minY2 - maxY1 > 0 ? ((minY1 - maxY2 > minY2 - maxY1) ? maxY2 - minY1 : minY2 - maxY1) : 0;
			var diffX = minX1 - maxX2 > 0 || minX2 - maxX1 > 0 ? ((minX1 - maxX2 > minX2 - maxX1) ? maxX2 - minX1 : minX2 - maxX1) : 0;
			var moveVector = Math.Abs(diffX) > Math.Abs(diffY) ? new IntVector2((int)(scale * diffX), 0) : new IntVector2(0, (int)(scale * diffY));
			var maxDiff = Math.Max(Math.Abs(diffX), Math.Abs(diffY));

			if (!firstChain && (maxDiff > minDiff || (maxDiff > 2 * avgSize && Random.NextDouble() < 0.5)))
			{
				newLayout = layout.Clone();

				foreach (var point in chain)
				{
					layout.GetConfiguration(point, out var conf);
					newLayout.SetConfiguration(point, new Configuration(conf.Polygon, conf.Position + moveVector));
				}

				return true;
			}

			newLayout = null;
			return false;
		}

		protected override Layout AddChainToLayout(Layout layout, List<int> chain)
		{
			var newLayout = layout.Clone();

			foreach (var node in chain)
			{
				AddNode(newLayout, node);
			}

			// New validity vectors were set to all ones
			RecomputeValidityVectors(newLayout);

			// New energies were set to zeros
			RecomputeEnergies(newLayout);

			return newLayout;
		}

		protected override Layout GetInitialLayout(List<int> chain)
		{
			var layout = new Layout(Graph.VerticesCount);
			return layout;
		}

		protected void AddNode(Layout layout, int vertex)
		{
			var configurations = new List<Configuration>();
			var neighbours = Graph.GetNeighbours(vertex);

			foreach (var node1 in neighbours)
			{
				if (layout.GetConfiguration(node1, out var configuration))
				{
					configurations.Add(configuration);
				}
			}

			if (configurations.Count == 0)
			{
				layout.SetConfiguration(vertex, new Configuration(ConfigurationSpaces.GetRandomShape(), new IntVector2()));
				return;
			}

			var bestEnergy = float.MaxValue;
			GridPolygon bestShape = null;
			var bestPosition = new IntVector2();

			foreach (var shape in ConfigurationSpaces.GetAllShapes())
			{
				var intersection = ConfigurationSpaces.GetMaximumIntersection(configurations, new Configuration(shape, new IntVector2()));

				foreach (var position in intersection)
				{
					var energy = GetEnergy(layout, vertex, new Configuration(shape, position), out var nIntersection, out var distance);

					if (energy < bestEnergy)
					{
						bestEnergy = energy;
						bestShape = shape;
						bestPosition = position;
					}
				}
			}

			var newConfiguration = new Configuration(bestShape, bestPosition);
			layout.SetConfiguration(vertex, newConfiguration);
		}

		protected Layout PerturbPosition(Layout layout, int node)
		{
			var configurations = new List<Configuration>();

			foreach (var node1 in Graph.GetNeighbours(node))
			{
				if (layout.GetConfiguration(node1, out var configuration))
				{
					configurations.Add(configuration);
				}
			}

			layout.GetConfiguration(node, out var mainConfiguration);

			var newPosition = ConfigurationSpaces.GetRandomIntersection(configurations, mainConfiguration);
			var newConfiguration = new Configuration(mainConfiguration, newPosition);

			return UpdateLayoutAfterPerturabtion(layout, node, newConfiguration);
		}

		protected Layout PerturbShape(Layout layout, int node)
		{
			// Choose new shape
			layout.GetConfiguration(node, out var configuration);
			GridPolygon polygon;
			do
			{
				polygon = ConfigurationSpaces.GetRandomShape();
			}
			while (ReferenceEquals(polygon, configuration.Polygon));

			var newConfiguration = new Configuration(configuration, polygon);

			return UpdateLayoutAfterPerturabtion(layout, node, newConfiguration);
		}

		protected Layout UpdateLayoutAfterPerturabtion(Layout layout, int node, Configuration configuration)
		{
			// Prepare new layout with temporary configuration to compute energies
			layout.GetConfiguration(node, out var oldConfiguration);
			var newLayout = layout.Clone();
			newLayout.SetConfiguration(node, configuration);

			// Recalculate validities
			var validityVector = configuration.InvalidNeigbours;
			var neigbours = Graph.GetNeighbours(node);

			// Check all neighbours whether their validity changed because of the currently perturbed node
			for (var i = 0; i < neigbours.Count; i++)
			{
				var neighbour = neigbours[i];
				if (!layout.GetConfiguration(neighbour, out var nc))
					continue;

				var nValidityVector = nc.InvalidNeigbours;
				var isValid = ConfigurationSpaces.HaveValidPosition(configuration, nc);
				var neighbourIndex = Graph.NeigbourIndex(node, neighbour);

				// We must check changes
				// Invalid neighbours must checked even without changes because their energy could change
				if (nValidityVector[1 << neighbourIndex] != !isValid || nValidityVector[1 << neighbourIndex])
				{
					nValidityVector[1 << neighbourIndex] = !isValid;
					validityVector[1 << i] = !isValid;

					var nNewEnergy = GetEnergy(newLayout, neighbour, nc, out var nIntersection, out var nDistance);
					var nNewConfiguration = new Configuration(nc.Polygon, nc.Position, nNewEnergy, nValidityVector, nIntersection, nDistance);
					newLayout.SetConfiguration(neighbour, nNewConfiguration);
				}
			}

			for (var i = 0; i < Graph.VerticesCount; i++)
			{
				if (i == node)
					continue;

				if (neigbours.Contains(i))
					continue;

				if (!layout.GetConfiguration(i, out var nc))
					continue;

				var nNewConfiguration = RecomputeEnergy(oldConfiguration, configuration, nc);
				newLayout.SetConfiguration(i, nNewConfiguration);
			}

			var newEnergy = GetEnergy(newLayout, node, configuration, out var intersection, out var distance);
			var newConfiguration = new Configuration(configuration.Polygon, configuration.Position, newEnergy, validityVector,
				intersection, distance);
			newLayout.SetConfiguration(node, newConfiguration);

			return newLayout;
		}

		protected Configuration RecomputeEnergy(Configuration oldConfiguration, Configuration newConfiguration,
			Configuration configuration)
		{
			var areaOld = PolygonOverlap.OverlapArea(configuration.Polygon, configuration.Position, oldConfiguration.Polygon, oldConfiguration.Position);
			var areaNew = PolygonOverlap.OverlapArea(configuration.Polygon, configuration.Position, newConfiguration.Polygon, newConfiguration.Position);
			var areaTotal = configuration.Area + (areaNew - areaOld);
			var newEnergy = (float)(Math.Pow(Math.E, areaTotal / sigma) * Math.Pow(Math.E, configuration.MoveDistance / sigma) - 1);

			return new Configuration(configuration, newEnergy, areaTotal, configuration.MoveDistance);
		}

		protected override bool IsLayoutValid(Layout layout)
		{
			if (!layout.AreConfigurationsValid())
			{
				return false;
			}

			/*var vertices = Graph.VerticesCount;

			// TODO: this is still slow
			for (var i = 0; i < vertices; i++)
			{
				for (var j = i + 1; j < vertices; j++)
				{
					if (!layout.GetConfiguration(i, out var c1) || !layout.GetConfiguration(j, out var c2)) continue;

					if (PolygonOverlap.DoOverlap(c1.Polygon, c1.Position, c2.Polygon, c2.Position))
					{
						return false;
					}
				}
			}*/

			return layout.GetEnergy() == 0f;
		}

		protected override void AddDoors(List<Layout> layouts)
		{
			var doors = ConfigurationSpaces.GetAllShapes().Distinct().ToDictionary(x => x, x => GetDoors(x, 1));
			layouts.ForEach(x => AddDoors(x, doors));
		}

		private void AddDoors(Layout layout, Dictionary<GridPolygon, List<IntLine>> doorsDict)
		{
			var doors = new List<IntLine>();

			for (var v1 = 0; v1 < Graph.VerticesCount; v1++)
			{
				foreach (var v2 in Graph.GetNeighbours(v1))
				{
					if (v2 <= v1) continue;

					if (!layout.GetConfiguration(v1, out var c1) || !layout.GetConfiguration(v2, out var c2))
					{
						continue;
					}

					var doors1 = doorsDict[c1.Polygon].Select(x => new IntLine(x.From + c1.Position, x.To + c1.Position)).ToList();
					var doors2 = doorsDict[c2.Polygon].Select(x => new IntLine(x.From + c2.Position, x.To + c2.Position)).ToList();
					var found = new List<IntLine>();

					foreach (var door in doors1)
					{
						if (doors2.Contains(door.SwitchOrientation()) || doors2.Contains(door))
						{
							found.Add(door);
							break;
						}
					}

					if (found.Count == 0)
					{
						throw new InvalidOperationException();
					}

					doors.Add(found.GetRandom(Random));
				}
			}

			layout.SetDoors(doors);
		}

		private List<IntLine> GetDoors(GridPolygon polygon, int doorsMargin = 1, int doorsLength = 1)
		{
			return polygon
				.GetLines()
				.Where(x => x.Length > 2 * doorsMargin)
				.Select(x => x.Shrink(doorsMargin))
				.Select(x => GetDoors(x, doorsLength))
				.SelectMany(x => x)
				.ToList();
		}

		private List<IntLine> GetDoors(IntLine line, int doorsLength = 1)
		{
			var doors = new List<IntLine>();

			for (var i = 0; i <= line.Length - doorsLength; i++)
			{
				doors.Add(line.Shrink(i, line.Length - doorsLength - i));
			}

			return doors;
		}

		/// <summary>
		/// Recompute all validity vectors.
		/// </summary>
		/// <remarks>
		/// TODO: all configuration pairs are now checked twice - coud be optimized
		/// </remarks>
		/// <param name="layout"></param>
		protected void RecomputeValidityVectors(Layout layout)
		{
			for (var vertex = 0; vertex < Graph.VerticesCount; vertex++)
			{
				if (!layout.GetConfiguration(vertex, out var configuration))
					continue;

				var neighbours = Graph.GetNeighbours(vertex);

				if (neighbours.Count == 0)
				{
					throw new InvalidOperationException(); // TODO: shift will not work
				}

				var validityVector = new BitVector32(int.MaxValue >> (32 - neighbours.Count - 1)); // TODO: check if it works

				for (var i = 0; i < neighbours.Count; i++)
				{
					var neighbour = neighbours[i];

					// Non-existent neighbour is the same thing as a valid neighbour
					if (!layout.GetConfiguration(neighbour, out var nc))
					{
						validityVector[1 << i] = false;
						continue;
					}

					var isValid = ConfigurationSpaces.HaveValidPosition(configuration, nc);
					validityVector[1 << i] = !isValid;
				}

				layout.SetConfiguration(vertex, new Configuration(configuration, validityVector));
			}
		}

		/// <summary>
		/// Compute energies of all vertices
		/// </summary>
		/// <remarks>
		/// Validity vectors must be up to date for this to work.
		/// </remarks>
		/// <param name="layout"></param>
		/// <returns></returns>
		protected void RecomputeEnergies(Layout layout)
		{
			for (var vertex = 0; vertex < Graph.VerticesCount; vertex++)
			{
				if (!layout.GetConfiguration(vertex, out var configuration))
					continue;

				var energy = GetEnergy(layout, vertex, configuration, out var intersection, out var distance, false);
				var newConfiguration = new Configuration(configuration, energy, intersection, distance);

				layout.SetConfiguration(vertex, newConfiguration);
			}
		}

		/// <summary>
		/// Compute energy of given vertex
		/// </summary>
		/// <remarks>
		/// Validity vectors must be up to date for this to work.
		/// </remarks>
		/// <param name="layout"></param>
		/// <param name="vertex"></param>
		/// <param name="configuration"></param>
		/// <param name="checkValid"></param>
		/// <returns></returns>
		protected float GetEnergy(Layout layout, int vertex, Configuration configuration, out int intersection, out float distance, bool checkValid = true)
		{
			intersection = 0;
			distance = 0f;

			var validityVector = configuration.InvalidNeigbours;
			var neighbours = Graph.GetNeighbours(vertex);

			for (var i = 0; i < Graph.VerticesCount; i++)
			{
				if (i == vertex)
					continue;

				if (!layout.GetConfiguration(i, out var c))
					continue;

				var area = PolygonOverlap.OverlapArea(configuration.Polygon, configuration.Position, c.Polygon, c.Position);

				if (area != 0)
				{
					intersection += area;
				}
			}

			for (var i = 0; i < neighbours.Count; i++)
			{
				var neighbour = neighbours[i];

				if (!layout.GetConfiguration(neighbour, out var c))
					continue;

				// Valid neighbours do not add anything to the energy
				if (!checkValid && validityVector[1 << i] == false)
					continue;

				if (!PolygonOverlap.DoTouch(configuration.Polygon, configuration.Position, c.Polygon, c.Position))
				{
					distance += (float) Math.Pow(IntVector2.ManhattanDistance(configuration.Polygon.BoundingRectangle.Center + configuration.Position,
						c.Polygon.BoundingRectangle.Center + c.Position), 2);
				}
			}

			return (float)(Math.Pow(Math.E, intersection / sigma) * Math.Pow(Math.E, distance / sigma) - 1);
		}

		protected override List<List<int>> GetChains(FastGraph<int> graph)
		{
			return GraphDecomposer.GetChains(graph);
		}
	}
}
