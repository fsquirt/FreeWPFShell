import os
from PyQt6.QtWidgets import QApplication
from PyQt6.QtSvg import QSvgRenderer
from PyQt6.QtGui import QImage, QPainter, QColor
from PyQt6.QtCore import QSize, Qt

def convert_svg_qt(directory="."):
    app = QApplication([])  # 初始化 Qt 应用
    svg_files = [f for f in os.listdir(directory) if f.lower().endswith('.svg')]

    for file_name in svg_files:
        svg_path = os.path.join(directory, file_name)
        png_name = os.path.splitext(file_name)[0] + ".png"
        png_path = os.path.join(directory, png_name)

        try:
            renderer = QSvgRenderer(svg_path)
            if not renderer.isValid():
                print(f"❌ 无法解析 SVG: {file_name}")
                continue

            # 获取默认尺寸
            size = renderer.defaultSize()
            # 创建带 alpha 透明通道的画布
            image = QImage(size, QImage.Format.Format_ARGB32)
            image.fill(Qt.GlobalColor.transparent)

            painter = QPainter(image)
            renderer.render(painter)
            painter.end()

            image.save(png_path)
            print(f"✅ 转换成功: {file_name} -> {png_name}")
        except Exception as e:
            print(f"❌ 转换失败: {file_name}, 错误: {e}")

if __name__ == "__main__":
    convert_svg_qt()