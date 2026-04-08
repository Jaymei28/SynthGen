import os
import tarfile
import shutil
import sys

def extract_unitypackage(package_path, output_dir):
    """
    Extracts a .unitypackage file to a structured directory.
    """
    if not os.path.exists(package_path):
        print(f"Error: {package_path} not found.")
        return

    temp_extract_dir = "temp_unity_extract"
    if os.path.exists(temp_extract_dir):
        shutil.rmtree(temp_extract_dir)
    os.makedirs(temp_extract_dir)

    print(f"Reading {package_path}...")
    try:
        with tarfile.open(package_path, "r:gz") as tar:
            tar.extractall(path=temp_extract_dir)
    except Exception as e:
        print(f"Failed to open/extract tar: {e}")
        return

    print("Reconstructing file structure...")
    for guid_folder in os.listdir(temp_extract_dir):
        guid_path = os.path.join(temp_extract_dir, guid_folder)
        if not os.path.isdir(guid_path):
            continue

        pathname_file = os.path.join(guid_path, "pathname")
        asset_file = os.path.join(guid_path, "asset")

        if os.path.exists(pathname_file) and os.path.exists(asset_file):
            with open(pathname_file, "r", encoding="utf-8") as f:
                target_path = f.read().strip()
            
            # Create full target path
            full_target_path = os.path.join(output_dir, target_path)
            os.makedirs(os.path.dirname(full_target_path), exist_ok=True)
            
            print(f"Extracting: {target_path}")
            shutil.copy2(asset_file, full_target_path)

    # Cleanup
    shutil.rmtree(temp_extract_dir)
    print(f"\nDone! Assets extracted to: {output_dir}")

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python unity_extractor.py <path_to_unitypackage> [output_directory]")
    else:
        pkg = sys.argv[1]
        out = sys.argv[2] if len(sys.argv) > 2 else "extracted_assets"
        extract_unitypackage(pkg, out)
