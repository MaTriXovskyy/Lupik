SELECT extension, COUNT(*) AS files
FROM previews
WHERE opened_at >= '2026-01-01'
GROUP BY extension
ORDER BY files DESC;
