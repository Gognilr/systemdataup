-- Integration test fixtures for second-batch endpoints (idempotent)
BEGIN;

-- 1. Client (online)
INSERT INTO clients (id, machine_id, hostname, display_name, os_name, agent_version, status, approved_at, last_heartbeat_at)
VALUES ('f0000001-0000-0000-0000-000000000001', 'TEST-MACHINE-001', 'test-host-01', 'Integration Test Host',
        'Windows Server 2022', '1.0.0', 'online', now(), now())
ON CONFLICT (id) DO NOTHING;

-- 2. Backup task
INSERT INTO backup_tasks (id, client_id, name, application_name, source_path, recognizer_type, task_mode, enabled)
VALUES ('f0000002-0000-0000-0000-000000000001', 'f0000001-0000-0000-0000-000000000001',
        'Integration Test Task', 'TestApp', 'E:\TestData', 'latest_directory', 'automatic', true)
ON CONFLICT (id) DO NOTHING;

-- 3. Candidate backup set
INSERT INTO candidate_backup_sets (id, client_id, task_id, candidate_key, source_root, precheck_status, total_files, total_bytes)
VALUES ('f0000003-0000-0000-0000-000000000001', 'f0000001-0000-0000-0000-000000000001',
        'f0000002-0000-0000-0000-000000000001', 'itest-candidate-001', 'E:\TestData', 'passed', 3, 1048724)
ON CONFLICT (id) DO NOTHING;

-- 4. Upload session (committed)
INSERT INTO upload_sessions (id, client_id, task_id, candidate_backup_set_id, status, total_files, total_bytes,
                             uploaded_bytes, verified_bytes, started_at, completed_at, verified_at, committed_at)
VALUES ('f0000004-0000-0000-0000-000000000001', 'f0000001-0000-0000-0000-000000000001',
        'f0000002-0000-0000-0000-000000000001', 'f0000003-0000-0000-0000-000000000001', 'committed',
        3, 1048724, 1048724, 1048724, now(), now(), now(), now())
ON CONFLICT (id) DO NOTHING;

-- 5. Backup set (available)
INSERT INTO backup_sets (id, client_id, task_id, source_candidate_id, upload_session_id, backup_set_code,
                         status, discovered_at, uploaded_at, verified_at, repository_path, total_files, total_bytes)
VALUES ('f0000005-0000-0000-0000-000000000001', 'f0000001-0000-0000-0000-000000000001',
        'f0000002-0000-0000-0000-000000000001', 'f0000003-0000-0000-0000-000000000001',
        'f0000004-0000-0000-0000-000000000001', 'TEST-SET-20260805-001', 'available',
        now(), now(), now(), 'E:\BackupRepository\testset1', 3, 1048724)
ON CONFLICT (id) DO NOTHING;

-- 6. Backup files (hashes match E:\BackupRepository\testset1)
INSERT INTO backup_files (backup_set_id, relative_path, file_name, size_bytes, last_modified_at, sha256, repository_relative_path, verification_status)
VALUES
 ('f0000005-0000-0000-0000-000000000001', 'data/app.db', 'app.db', 1048576, now(),
  '00f4b96e162f33ab0e942d1fd2dc77b0d5a4ad418b4acf4a976ae4c68f37507e', 'data/app.db', 'verified'),
 ('f0000005-0000-0000-0000-000000000001', 'logs/run.log', 'run.log', 131, now(),
  '7660802527f3d893d0a0edee676b7f68489a61186b0a693ce2569d15eda2d38c', 'logs/run.log', 'verified'),
 ('f0000005-0000-0000-0000-000000000001', 'readme.txt', 'readme.txt', 17, now(),
  'de29c4a911fa4ef29237f4a6f52727a56aea688716a47a1e0d4d8528a7c2e854', 'readme.txt', 'verified')
ON CONFLICT (backup_set_id, relative_path) DO NOTHING;

COMMIT;

SELECT 'client='||(SELECT status FROM clients WHERE id='f0000001-0000-0000-0000-000000000001')
    ||' set='||(SELECT status FROM backup_sets WHERE id='f0000005-0000-0000-0000-000000000001')
    ||' files='||(SELECT count(*) FROM backup_files WHERE backup_set_id='f0000005-0000-0000-0000-000000000001') AS fixture;
