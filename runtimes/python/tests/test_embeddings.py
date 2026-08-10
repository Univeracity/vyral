from __future__ import annotations

import asyncio
import math
import unittest

from vyral_runtime import (
    DeterministicHashEmbeddingProvider,
    EmbeddingProviderRegistry,
    EmbeddingService,
    LocalTokenHashEmbeddingProvider,
    normalize_embedding_purpose,
    prepare_embedding_text,
)


def cosine(left: tuple[float, ...], right: tuple[float, ...]) -> float:
    numerator = sum(first * second for first, second in zip(left, right))
    left_norm = math.sqrt(sum(value * value for value in left))
    right_norm = math.sqrt(sum(value * value for value in right))
    return numerator / (left_norm * right_norm)


class EmbeddingProviderTests(unittest.TestCase):
    def test_deterministic_hash_matches_dotnet_float32_vector(self) -> None:
        provider = DeterministicHashEmbeddingProvider(dimensions=8)

        first = provider.generate_embedding("Vyral π")
        second = provider.generate_embedding("Vyral π")

        self.assertEqual(first, second)
        self.assertEqual(
            (
                -0.3147768974304199,
                -0.00797050166875124,
                -0.6145191788673401,
                -0.01620689034461975,
                0.30283603072166443,
                0.37020501494407654,
                -0.2383783459663391,
                -0.4872061610221863,
            ),
            first,
        )
        self.assertAlmostEqual(1.0, math.sqrt(sum(value * value for value in first)), places=6)

    def test_token_hash_is_deterministic_and_overlap_sensitive(self) -> None:
        provider = LocalTokenHashEmbeddingProvider(dimensions=64)
        query = provider.generate_embedding("portable local retrieval")
        related = provider.generate_embedding("local retrieval is portable")
        unrelated = provider.generate_embedding("astronomy telescope orbit")

        self.assertEqual(query, provider.generate_embedding("portable local retrieval"))
        self.assertGreater(cosine(query, related), cosine(query, unrelated))
        self.assertAlmostEqual(1.0, math.sqrt(sum(value * value for value in query)), places=6)
        self.assertEqual(64, len(query))

    def test_registry_is_sorted_case_insensitive_and_extensible(self) -> None:
        registry = EmbeddingProviderRegistry()

        self.assertEqual(
            ("deterministic-hash", "local-token-hash"),
            tuple(item.provider for item in registry.get_providers()),
        )
        provider = registry.create(
            {
                "provider": "DETERMINISTIC-HASH",
                "dimensions": 12,
                "modelId": "fixture",
            }
        )
        self.assertEqual(12, provider.dimensions)
        self.assertEqual("fixture", provider.model_id)
        with self.assertRaisesRegex(ValueError, "Registered providers"):
            registry.create({"provider": "missing"})

    def test_embedding_service_batches_prefixes_and_supports_async(self) -> None:
        service = EmbeddingService(
            provider_options={
                "provider": "deterministic-hash",
                "dimensions": 8,
                "queryPrefix": "query: ",
            }
        )

        response = service.embed(
            {
                "text": "already",
                "texts": ["query: prepared"],
                "purpose": "query",
            }
        )
        asynchronous = asyncio.run(
            service.aembed({"text": "already", "purpose": "query"})
        )

        self.assertEqual("query", response.purpose)
        self.assertEqual(2, len(response.items))
        self.assertTrue(response.items[0].prefix_applied)
        self.assertFalse(response.items[1].prefix_applied)
        self.assertEqual(len("query: "), response.items[0].prefix_length)
        self.assertEqual(response.items[0].values, asynchronous.items[0].values)

    def test_embedding_request_limits_and_purpose_are_explicit(self) -> None:
        service = EmbeddingService(
            provider=DeterministicHashEmbeddingProvider(dimensions=4)
        )
        self.assertEqual("symmetric", normalize_embedding_purpose(None))
        prepared = prepare_embedding_text(
            "passage: text",
            "document",
            None,
            "passage: ",
            None,
        )
        self.assertFalse(prepared.prefix_applied)
        self.assertEqual("document", prepared.purpose)

        invalid: tuple[dict[str, object], ...] = (
            {},
            {"text": " "},
            {"texts": ["valid"] * 129},
            {"text": "x" * 100_001},
            {"text": "valid", "purpose": "unknown"},
        )
        for request in invalid:
            with self.subTest(request=list(request)[:2]):
                with self.assertRaises(ValueError):
                    service.embed(request)


if __name__ == "__main__":
    unittest.main()
